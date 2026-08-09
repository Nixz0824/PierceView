using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal sealed class DwmPortalOverlay : IDisposable
{
    private readonly int _radius;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private PortalOverlayManager? _manager;
    private ApplicationContext? _applicationContext;
    private Exception? _startupException;
    private bool _disposed;

    internal DwmPortalOverlay(int radius)
    {
        _radius = radius;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "WindowPortal DWM compositor"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_startupException is not null)
        {
            throw new InvalidOperationException("无法启动视觉穿透合成器。", _startupException);
        }
    }

    internal bool IsVisible => Invoke(manager => manager.PortalVisible);

    internal nint SourceWindow => Invoke(manager => manager.SourceWindow);

    internal int VisibleLayerCount => Invoke(manager => manager.VisibleLayerCount);

    internal string CompatibilitySummary => Invoke(manager => manager.CompatibilitySummary);

    internal int ForegroundRecoveryCount =>
        Invoke(manager => manager.ForegroundRecoveryCount);

    internal int BackgroundPromotionCount =>
        Invoke(manager => manager.BackgroundPromotionCount);

    internal bool TryShow(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point screenCenter,
        out string? error)
    {
        if (sourceWindow == nint.Zero || !NativeMethods.IsWindow(sourceWindow))
        {
            error = "圆洞下方没有可用于视觉预览的窗口。";
            return false;
        }

        try
        {
            var result = Invoke(manager => manager.TryShowPortal(
                    sourceWindow,
                    protectedWindow,
                    screenCenter,
                    _radius,
                    out var detail)
                ? (Success: true, Detail: (string?)null)
                : (Success: false, Detail: detail));
            error = result.Detail;
            return result.Success;
        }
        catch (Exception exception)
        {
            error = $"无法显示视觉穿透合成器：{exception.Message}";
            return false;
        }
    }

    internal bool TryUpdate(NativeMethods.Point screenCenter, out string? error)
    {
        try
        {
            var result = Invoke(manager => manager.TryUpdatePortal(
                    screenCenter,
                    _radius,
                    out var detail)
                ? (Success: true, Detail: (string?)null)
                : (Success: false, Detail: detail));
            error = result.Detail;
            return result.Success;
        }
        catch (Exception exception)
        {
            error = $"无法更新视觉穿透合成器：{exception.Message}";
            return false;
        }
    }

    internal void Hide()
    {
        if (_disposed || _manager is null)
        {
            return;
        }

        try
        {
            Invoke(manager =>
            {
                manager.HidePortal();
                return true;
            });
        }
        catch
        {
            // Emergency restoration continues even if the compositor loop is gone.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;

        if (_manager is not null && _applicationContext is not null)
        {
            try
            {
                _manager.BeginInvoke(() => _applicationContext.ExitThread());
            }
            catch
            {
                // Process shutdown may have already stopped the message loop.
            }
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
        GC.SuppressFinalize(this);
    }

    private T Invoke<T>(Func<PortalOverlayManager, T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var manager = _manager ?? throw new InvalidOperationException("视觉穿透合成器尚未就绪。");

        if (manager.InvokeRequired)
        {
            return (T)manager.Invoke(operation, manager)!;
        }

        return operation(manager);
    }

    private void RunMessageLoop()
    {
        try
        {
            _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            _applicationContext = new ApplicationContext();
            _manager = new PortalOverlayManager();
            _ = _manager.Handle;
            _ready.Set();
            Application.Run(_applicationContext);
        }
        catch (Exception exception)
        {
            _startupException = exception;
            _ready.Set();
        }
        finally
        {
            _manager?.Dispose();
            _manager = null;
            _applicationContext?.Dispose();
            _applicationContext = null;
        }
    }

    private sealed class PortalOverlayManager : Form
    {
        private const int MaxCompositedLayers = 3;
        private const int SceneChangeConfirmationFrames = 3;

        private readonly ForegroundZOrderGuard _foregroundGuard = new();
        private readonly System.Windows.Forms.Timer _zOrderGuardTimer;
        private PortalScene? _scene;
        private NativeMethods.Point? _lastScreenCenter;
        private nint[]? _pendingSceneWindows;
        private int _pendingSceneFrameCount;
        private nint _protectedWindow;
        private bool _portalVisible;

        internal PortalOverlayManager()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Opacity = 0;
            Size = new Size(1, 1);
            _zOrderGuardTimer = new System.Windows.Forms.Timer
            {
                Interval = 10
            };
            _zOrderGuardTimer.Tick += (_, _) => _foregroundGuard.EnsurePreserved();
        }

        internal bool PortalVisible => _portalVisible;

        internal nint SourceWindow => _scene?.PrimarySourceWindow ?? nint.Zero;

        internal int VisibleLayerCount => _scene?.RenderableLayerCount ?? 0;

        internal string CompatibilitySummary =>
            _scene?.CompatibilitySummary ?? "（合成器未启用）";

        internal int ForegroundRecoveryCount => _foregroundGuard.RecoveryCount;

        internal int BackgroundPromotionCount => _foregroundGuard.PromotionCount;

        internal bool TryShowPortal(
            nint sourceWindow,
            nint protectedWindow,
            NativeMethods.Point screenCenter,
            int radius,
            out string? error)
        {
            HidePortal();
            _protectedWindow = protectedWindow;

            if (!_foregroundGuard.TryEnable(
                    sourceWindow,
                    protectedWindow,
                    screenCenter,
                    radius,
                    out error))
            {
                HidePortal();
                return false;
            }

            var descriptors = DiscoverLayers(protectedWindow, screenCenter, radius);
            PortalScene? scene = null;
            if (descriptors.Count > 0 &&
                (!PortalScene.TryCreate(descriptors, radius, out scene, out _) ||
                 !scene.TryPrepare(screenCenter, radius, out _) ||
                 !TrySwapScenes(null, scene, screenCenter, radius, out _) ||
                 NativeMethods.DwmFlush() != 0))
            {
                scene?.Dispose();
                scene = null;
            }

            _scene = scene;
            _portalVisible = true;
            _lastScreenCenter = screenCenter;
            ResetPendingSceneChange();
            _zOrderGuardTimer.Start();
            _ = NativeMethods.DwmFlush();
            error = null;
            return true;
        }

        internal bool TryUpdatePortal(
            NativeMethods.Point screenCenter,
            int radius,
            out string? error)
        {
            _foregroundGuard.UpdatePortalGeometry(screenCenter, radius);
            _foregroundGuard.EnsurePreserved();
            var promotionOccurred = _foregroundGuard.TryTakePromotedWindow(
                out _,
                out var promotionError);

            if (!_portalVisible ||
                !NativeMethods.IsWindow(_protectedWindow))
            {
                error = "视觉穿透合成器已经不可用。";
                return false;
            }

            var descriptors = DiscoverLayers(_protectedWindow, screenCenter, radius);
            if (_scene is null)
            {
                ResetPendingSceneChange();
                if (descriptors.Count > 0)
                {
                    _ = TryInstallScene(descriptors, screenCenter, radius);
                }

                _lastScreenCenter = screenCenter;
                error = null;
                return true;
            }

            if (descriptors.Count == 0)
            {
                ResetPendingSceneChange();
                AdvanceOrDropCurrentScene(screenCenter, radius);
                error = null;
                return true;
            }

            if (_scene.Matches(descriptors))
            {
                ResetPendingSceneChange();
                AdvanceOrDropCurrentScene(screenCenter, radius);
                error = null;
                return true;
            }

            var mustSwitchImmediately =
                promotionOccurred ||
                !_scene.SourcesAreValid();
            if (mustSwitchImmediately || ConfirmPendingSceneChange(descriptors))
            {
                _ = TryInstallScene(descriptors, screenCenter, radius);
                ResetPendingSceneChange();
            }
            else
            {
                AdvanceOrDropCurrentScene(screenCenter, radius);
            }

            // A foreground-guard warning must not freeze the visual/physical hole at
            // different cursor samples. The guard retries independently on its timer.
            _ = promotionError;

            _lastScreenCenter = screenCenter;
            error = null;
            return true;
        }

        internal void HidePortal()
        {
            _zOrderGuardTimer.Stop();
            if (_scene is not null)
            {
                _ = TrySwapScenes(_scene, null, default, 0, out _);
            }

            _portalVisible = false;
            _scene?.Dispose();
            _scene = null;
            _foregroundGuard.Restore();
            _lastScreenCenter = null;
            ResetPendingSceneChange();
            _protectedWindow = nint.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            HidePortal();
            if (disposing)
            {
                _zOrderGuardTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void AdvanceOrDropCurrentScene(
            NativeMethods.Point screenCenter,
            int radius)
        {
            if (_scene is null)
            {
                _lastScreenCenter = screenCenter;
                return;
            }

            if (_lastScreenCenter == screenCenter && !_scene.SourceGeometryChanged())
            {
                return;
            }

            var prepared = _scene.TryPrepare(screenCenter, radius, out _);
            if (TryMoveScene(_scene, screenCenter, radius, out _))
            {
                _lastScreenCenter = screenCenter;
                if (prepared)
                {
                    _ = NativeMethods.DwmFlush();
                }

                return;
            }

            var failedScene = _scene;
            _ = TrySwapScenes(failedScene, null, default, 0, out _);
            _scene = null;
            failedScene.Dispose();
            _lastScreenCenter = screenCenter;
            _ = NativeMethods.DwmFlush();
        }

        private bool TryInstallScene(
            IReadOnlyList<PortalLayerDescriptor> descriptors,
            NativeMethods.Point screenCenter,
            int radius)
        {
            // Keep the outgoing and incoming forms on the exact same sample.
            // Move only the outgoing HWNDs here: updating their live thumbnails
            // during retirement can expose DWM's black intermediate surface.
            // Stale content is safe for this sub-frame hand-off and remains the
            // last-known-good visual until the replacement is warm.
            if (_scene is not null)
            {
                if (TryMoveScene(_scene, screenCenter, radius, out _))
                {
                    _lastScreenCenter = screenCenter;
                }
            }

            var previousScene = _scene;
            if (!PortalScene.TryCreate(descriptors, radius, out var nextScene, out _) ||
                !nextScene.TryPrepare(screenCenter, radius, out _) ||
                (previousScene is not null && !nextScene.TrySetGlobalAlpha(1, out _)) ||
                // Present and warm the replacement while the last-known-good
                // scene is still visible. A hidden destination window does not
                // reliably receive a ready DWM thumbnail before its first frame.
                !TrySwapScenes(null, nextScene, screenCenter, radius, out _) ||
                NativeMethods.DwmFlush() != 0 ||
                (previousScene is not null &&
                 (!nextScene.TrySetGlobalAlpha(byte.MaxValue, out _) ||
                  NativeMethods.DwmFlush() != 0)))
            {
                nextScene?.Dispose();
                AdvanceOrDropCurrentScene(screenCenter, radius);
                return false;
            }

            _scene = nextScene;
            _lastScreenCenter = screenCenter;

            // The new forms fully cover the same portal bounds. Retiring the old
            // forms only after the flush removes the one-frame empty hand-off
            // that appeared as a black flash or a transient rectangle.
            previousScene?.Dispose();
            _ = NativeMethods.DwmFlush();
            return true;
        }

        private bool ConfirmPendingSceneChange(
            IReadOnlyList<PortalLayerDescriptor> descriptors)
        {
            var windows = descriptors.Select(descriptor => descriptor.Window).ToArray();
            if (_pendingSceneWindows is null ||
                !_pendingSceneWindows.SequenceEqual(windows))
            {
                _pendingSceneWindows = windows;
                _pendingSceneFrameCount = 1;
                return false;
            }

            _pendingSceneFrameCount++;
            return _pendingSceneFrameCount >= SceneChangeConfirmationFrames;
        }

        private void ResetPendingSceneChange()
        {
            _pendingSceneWindows = null;
            _pendingSceneFrameCount = 0;
        }

        private static List<PortalLayerDescriptor> DiscoverLayers(
            nint protectedWindow,
            NativeMethods.Point screenCenter,
            int radius)
        {
            var result = new List<PortalLayerDescriptor>(MaxCompositedLayers);
            var portalBounds = WindowRegionController.CreateHoleBounds(screenCenter, radius);

            for (var window = NativeMethods.GetWindow(
                     protectedWindow,
                     NativeMethods.GwHwndNext);
                 window != nint.Zero && result.Count < MaxCompositedLayers;
                 window = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext))
            {
                var decision = CompatibilityPolicy.Evaluate(window, protectedWindow);
                if (!decision.IncludeInVisualStack ||
                    !NativeMethods.GetWindowRect(window, out var windowRect) ||
                    !Intersects(windowRect, portalBounds))
                {
                    continue;
                }

                result.Add(new PortalLayerDescriptor(window, decision));
            }

            return result;
        }

        private static bool TryMoveScene(
            PortalScene scene,
            NativeMethods.Point center,
            int radius,
            out string? error)
        {
            var forms = scene.Forms;
            if (forms.Count == 0)
            {
                error = null;
                return true;
            }

            var position = NativeMethods.BeginDeferWindowPos(forms.Count);
            if (position == nint.Zero)
            {
                error = LastWin32Error("无法开始多层透视圆的同步移动");
                return false;
            }

            foreach (var form in forms)
            {
                position = NativeMethods.DeferWindowPos(
                    position,
                    form.Handle,
                    nint.Zero,
                    center.X - radius,
                    center.Y - radius,
                    0,
                    0,
                    NativeMethods.SwpNoZOrder |
                    NativeMethods.SwpNoSize |
                    NativeMethods.SwpNoActivate |
                    NativeMethods.SwpNoOwnerZOrder);
                if (position == nint.Zero)
                {
                    error = LastWin32Error("无法同步移动所有透视图层");
                    return false;
                }
            }

            if (!NativeMethods.EndDeferWindowPos(position))
            {
                error = LastWin32Error("无法提交多层透视圆的同步移动");
                return false;
            }

            error = null;
            return true;
        }

        private static bool TrySwapScenes(
            PortalScene? previousScene,
            PortalScene? nextScene,
            NativeMethods.Point center,
            int radius,
            out string? error)
        {
            var previousForms = previousScene?.Forms ?? [];
            var nextForms = nextScene?.Forms ?? [];
            var windowCount = previousForms.Count + nextForms.Count;
            if (windowCount == 0)
            {
                error = null;
                return true;
            }

            var position = NativeMethods.BeginDeferWindowPos(windowCount);
            if (position == nint.Zero)
            {
                error = LastWin32Error("无法开始多层合成场景切换");
                return false;
            }

            var commonFlags =
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoOwnerZOrder;
            foreach (var form in previousForms)
            {
                position = NativeMethods.DeferWindowPos(
                    position,
                    form.Handle,
                    NativeMethods.HwndTopMost,
                    0,
                    0,
                    0,
                    0,
                    commonFlags |
                    NativeMethods.SwpNoMove |
                    NativeMethods.SwpNoSize |
                    NativeMethods.SwpHideWindow);
                if (position == nint.Zero)
                {
                    error = LastWin32Error("无法隐藏旧的多层合成场景");
                    return false;
                }
            }

            var diameter = radius <= 0 ? 1 : checked((radius * 2) + 1);
            nint insertAfter = NativeMethods.HwndTopMost;
            foreach (var form in nextForms)
            {
                position = NativeMethods.DeferWindowPos(
                    position,
                    form.Handle,
                    insertAfter,
                    center.X - radius,
                    center.Y - radius,
                    diameter,
                    diameter,
                    commonFlags | NativeMethods.SwpShowWindow);
                if (position == nint.Zero)
                {
                    error = LastWin32Error("无法显示新的多层合成场景");
                    return false;
                }

                insertAfter = form.Handle;
            }

            if (!NativeMethods.EndDeferWindowPos(position))
            {
                error = LastWin32Error("无法提交多层合成场景切换");
                return false;
            }

            error = null;
            return true;
        }

        private static bool Intersects(NativeMethods.Rect first, NativeMethods.Rect second) =>
            first.Left < second.Right &&
            first.Right > second.Left &&
            first.Top < second.Bottom &&
            first.Bottom > second.Top;

        private static string LastWin32Error(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return error == 0
                ? message
                : $"{message}：{new Win32Exception(error).Message}（{error}）";
        }
    }

    private sealed class PortalScene : IDisposable
    {
        private readonly List<PortalLayer> _layers;
        private NativeMethods.Rect[]? _lastSourceRects;

        private PortalScene(List<PortalLayer> layers)
        {
            _layers = layers;
        }

        internal IReadOnlyList<PortalLayerForm> Forms =>
            _layers
                .Where(layer => layer.Form is not null)
                .Select(layer => layer.Form!)
                .ToArray();

        internal nint PrimarySourceWindow =>
            _layers.Count == 0 ? nint.Zero : _layers[0].Descriptor.Window;

        internal int RenderableLayerCount =>
            _layers.Count(layer => layer.Form is not null);

        internal string CompatibilitySummary => string.Join(
            "；",
            _layers.Select((layer, index) =>
                $"-{index + 1} {layer.Descriptor.Decision.ProcessName}: " +
                (layer.Form is null
                    ? $"未合成（{layer.Descriptor.Decision.Reason}）"
                    : "DWM 合成")));

        internal static bool TryCreate(
            IReadOnlyList<PortalLayerDescriptor> descriptors,
            int radius,
            out PortalScene scene,
            out string? error)
        {
            var layers = new List<PortalLayer>(descriptors.Count);
            foreach (var descriptor in descriptors)
            {
                PortalLayerForm? form = null;
                var effectiveDescriptor = descriptor;
                if (descriptor.Decision.AllowVisualPreview)
                {
                    form = new PortalLayerForm(radius);
                    if (!form.TryRegisterSource(descriptor.Window, out var registrationError))
                    {
                        form.Dispose();
                        form = null;
                        effectiveDescriptor = descriptor with
                        {
                            Decision = new WindowCompatibilityDecision(
                                WindowCompatibilityKind.VisualUnsupported,
                                IncludeInVisualStack: true,
                                AllowVisualPreview: false,
                                descriptor.Decision.AllowInteraction,
                                descriptor.Decision.ProcessName,
                                registrationError ?? "DWM 缩略图注册失败。")
                        };
                    }
                }

                layers.Add(new PortalLayer(effectiveDescriptor, form));
            }

            scene = new PortalScene(layers);
            error = null;
            return true;
        }

        internal bool Matches(IReadOnlyList<PortalLayerDescriptor> descriptors)
        {
            if (descriptors.Count != _layers.Count)
            {
                return false;
            }

            for (var index = 0; index < descriptors.Count; index++)
            {
                if (descriptors[index].Window != _layers[index].Descriptor.Window)
                {
                    return false;
                }
            }

            return true;
        }

        internal bool SourceGeometryChanged()
        {
            if (_lastSourceRects is null || _lastSourceRects.Length != _layers.Count)
            {
                return true;
            }

            for (var index = 0; index < _layers.Count; index++)
            {
                if (!NativeMethods.GetWindowRect(
                        _layers[index].Descriptor.Window,
                        out var currentRect) ||
                    currentRect != _lastSourceRects[index])
                {
                    return true;
                }
            }

            return false;
        }

        internal bool SourcesAreValid() =>
            _layers.All(layer =>
                NativeMethods.IsWindow(layer.Descriptor.Window) &&
                NativeMethods.IsWindowVisible(layer.Descriptor.Window));

        internal bool TrySetGlobalAlpha(byte alpha, out string? error)
        {
            foreach (var form in Forms)
            {
                if (!form.TrySetGlobalAlpha(alpha, out error))
                {
                    return false;
                }
            }

            error = null;
            return true;
        }

        internal bool TryPrepare(
            NativeMethods.Point screenCenter,
            int radius,
            out string? error)
        {
            var sourceRects = new NativeMethods.Rect[_layers.Count];
            for (var index = 0; index < _layers.Count; index++)
            {
                if (!NativeMethods.GetWindowRect(
                        _layers[index].Descriptor.Window,
                        out sourceRects[index]))
                {
                    error = $"无法读取第 {index + 1} 层后台窗口的位置。";
                    return false;
                }
            }

            for (var index = 0; index < _layers.Count; index++)
            {
                var form = _layers[index].Form;
                if (form is null)
                {
                    continue;
                }

                var unrenderableShallowerRects = Enumerable
                    .Range(0, index)
                    .Where(shallowIndex => _layers[shallowIndex].Form is null)
                    .Select(shallowIndex => sourceRects[shallowIndex])
                    .ToArray();
                if (!form.TryPrepare(
                        sourceRects[index],
                        // The portal forms already have the same shallow-to-deep
                        // Z-order as the source windows. Let normal window
                        // stacking occlude renderable layers. Only reserve the
                        // rectangles of unsupported shallower sources so deeper
                        // content cannot impersonate a protected/uncapturable app.
                        unrenderableShallowerRects,
                        screenCenter,
                        radius,
                        out error))
                {
                    return false;
                }
            }

            _lastSourceRects = sourceRects;
            error = null;
            return true;
        }

        public void Dispose()
        {
            foreach (var layer in _layers)
            {
                layer.Form?.Dispose();
            }

            _layers.Clear();
            _lastSourceRects = null;
        }
    }

    private sealed class PortalLayerForm : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmNcHitTest = 0x0084;
        private const int WmMouseActivate = 0x0021;
        private static readonly nint HtTransparent = new(-1);
        private static readonly nint MaNoActivate = new(3);

        private readonly int _diameter;
        private nint _thumbnail;
        private nint _regionProbe;
        private PortalRegionSignature? _lastRegionSignature;
        private int _lastRegionType = NativeMethods.ErrorRegion;
        private NativeMethods.Rect _lastRegionBounds;

        internal PortalLayerForm(int radius)
        {
            _diameter = checked((radius * 2) + 1);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            // SetWindowRgn provides the shape. Alpha-only WS_EX_LAYERED is retained
            // for reliable cross-process click-through, but no color key is used.
            BackColor = Color.Black;
            TopMost = true;
            Bounds = new Rectangle(0, 0, _diameter, _diameter);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |=
                    WsExTransparent |
                    WsExToolWindow |
                    WsExLayered |
                    WsExNoActivate;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs eventArgs)
        {
            base.OnHandleCreated(eventArgs);
            _ = NativeMethods.SetLayeredWindowAttributes(
                Handle,
                0,
                byte.MaxValue,
                NativeMethods.LwaAlpha);
        }

        internal bool TryRegisterSource(nint sourceWindow, out string? error)
        {
            var result = NativeMethods.DwmRegisterThumbnail(Handle, sourceWindow, out _thumbnail);
            if (result != 0 || _thumbnail == nint.Zero)
            {
                error = $"DwmRegisterThumbnail 失败：0x{result:X8}。";
                _thumbnail = nint.Zero;
                return false;
            }

            error = null;
            return true;
        }

        internal bool TrySetGlobalAlpha(byte alpha, out string? error)
        {
            if (!NativeMethods.SetLayeredWindowAttributes(
                    Handle,
                    0,
                    alpha,
                    NativeMethods.LwaAlpha))
            {
                error = LastWin32Error("无法设置多层透视场景的预热透明度");
                return false;
            }

            error = null;
            return true;
        }

        internal bool TryPrepare(
            NativeMethods.Rect sourceWindowRect,
            IReadOnlyList<NativeMethods.Rect> shallowerWindowRects,
            NativeMethods.Point screenCenter,
            int radius,
            out string? error)
        {
            if (_thumbnail == nint.Zero)
            {
                error = "DWM 图层尚未注册。";
                return false;
            }

            var portalBounds = WindowRegionController.CreateHoleBounds(screenCenter, radius);
            var visibleSource = Intersect(sourceWindowRect, portalBounds);
            var hasVisibleSource = visibleSource.Width > 0 && visibleSource.Height > 0;
            var destination = hasVisibleSource
                ? new NativeMethods.Rect(
                    visibleSource.Left - portalBounds.Left,
                    visibleSource.Top - portalBounds.Top,
                    visibleSource.Right - portalBounds.Left,
                    visibleSource.Bottom - portalBounds.Top)
                : new NativeMethods.Rect(0, 0, 0, 0);
            var source = hasVisibleSource
                ? new NativeMethods.Rect(
                    visibleSource.Left - sourceWindowRect.Left,
                    visibleSource.Top - sourceWindowRect.Top,
                    visibleSource.Right - sourceWindowRect.Left,
                    visibleSource.Bottom - sourceWindowRect.Top)
                : new NativeMethods.Rect(0, 0, 0, 0);
            var properties = new NativeMethods.DwmThumbnailProperties
            {
                Flags = NativeMethods.DwmTnpRectDestination |
                    NativeMethods.DwmTnpRectSource |
                    NativeMethods.DwmTnpOpacity |
                    NativeMethods.DwmTnpVisible |
                    NativeMethods.DwmTnpSourceClientAreaOnly,
                Destination = destination,
                Source = source,
                Opacity = byte.MaxValue,
                Visible = hasVisibleSource,
                SourceClientAreaOnly = false
            };

            var updateResult = NativeMethods.DwmUpdateThumbnailProperties(
                _thumbnail,
                ref properties);
            if (updateResult != 0)
            {
                error = $"DwmUpdateThumbnailProperties 失败：0x{updateResult:X8}。";
                return false;
            }

            var occluderClips = shallowerWindowRects
                .Select(rect => Intersect(rect, portalBounds))
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .Select(rect => new NativeMethods.Rect(
                    rect.Left - portalBounds.Left,
                    rect.Top - portalBounds.Top,
                    rect.Right - portalBounds.Left,
                    rect.Bottom - portalBounds.Top))
                .ToArray();
            var signature = new PortalRegionSignature(destination, occluderClips);
            if (_lastRegionSignature is null ||
                !_lastRegionSignature.Value.Equals(signature) ||
                !HasExpectedRegion())
            {
                if (!TryApplyRegion(
                        destination,
                        occluderClips,
                        out _lastRegionType,
                        out _lastRegionBounds,
                        out error))
                {
                    return false;
                }

                _lastRegionSignature = signature;
            }

            error = null;
            return true;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                message.Result = HtTransparent;
                return;
            }

            if (message.Msg == WmMouseActivate)
            {
                message.Result = MaNoActivate;
                return;
            }

            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            Hide();
            if (_thumbnail != nint.Zero)
            {
                _ = NativeMethods.DwmUnregisterThumbnail(_thumbnail);
                _thumbnail = nint.Zero;
            }

            if (_regionProbe != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(_regionProbe);
                _regionProbe = nint.Zero;
            }

            base.Dispose(disposing);
        }

        private bool TryApplyRegion(
            NativeMethods.Rect sourceClip,
            IReadOnlyList<NativeMethods.Rect> occluderClips,
            out int regionType,
            out NativeMethods.Rect regionBounds,
            out string? error)
        {
            regionType = NativeMethods.ErrorRegion;
            regionBounds = default;
            var region = NativeMethods.CreateEllipticRgn(0, 0, _diameter, _diameter);
            var sourceRegion = NativeMethods.CreateRectRgn(
                sourceClip.Left,
                sourceClip.Top,
                sourceClip.Right,
                sourceClip.Bottom);
            if (region == nint.Zero || sourceRegion == nint.Zero)
            {
                DeleteIfOwned(region);
                DeleteIfOwned(sourceRegion);
                error = LastWin32Error("无法创建多层透视圆的裁剪区域");
                return false;
            }

            var combineResult = NativeMethods.CombineRgn(
                region,
                region,
                sourceRegion,
                NativeMethods.RgnAnd);
            _ = NativeMethods.DeleteObject(sourceRegion);
            if (combineResult == NativeMethods.ErrorRegion)
            {
                _ = NativeMethods.DeleteObject(region);
                error = LastWin32Error("无法裁剪当前 DWM 图层");
                return false;
            }

            foreach (var occluderClip in occluderClips)
            {
                var occluderRegion = NativeMethods.CreateRectRgn(
                    occluderClip.Left,
                    occluderClip.Top,
                    occluderClip.Right,
                    occluderClip.Bottom);
                if (occluderRegion == nint.Zero)
                {
                    _ = NativeMethods.DeleteObject(region);
                    error = LastWin32Error("无法创建上层窗口的遮挡区域");
                    return false;
                }

                combineResult = NativeMethods.CombineRgn(
                    region,
                    region,
                    occluderRegion,
                    NativeMethods.RgnDiff);
                _ = NativeMethods.DeleteObject(occluderRegion);
                if (combineResult == NativeMethods.ErrorRegion)
                {
                    _ = NativeMethods.DeleteObject(region);
                    error = LastWin32Error("无法从深层画面中减去上层遮挡区域");
                    return false;
                }
            }

            regionType = NativeMethods.GetRgnBox(region, out regionBounds);
            if (regionType == NativeMethods.ErrorRegion)
            {
                _ = NativeMethods.DeleteObject(region);
                error = LastWin32Error("无法读取多层透视圆的最终裁剪范围");
                return false;
            }

            if (NativeMethods.SetWindowRgn(Handle, region, redraw: true) == 0)
            {
                _ = NativeMethods.DeleteObject(region);
                error = LastWin32Error("无法提交 DWM 图层的圆形区域");
                return false;
            }

            // SetWindowRgn succeeded and now owns the region handle.
            error = null;
            return true;
        }

        private bool HasExpectedRegion()
        {
            if (_lastRegionType == NativeMethods.ErrorRegion)
            {
                return false;
            }

            if (_regionProbe == nint.Zero)
            {
                _regionProbe = NativeMethods.CreateRectRgn(0, 0, 0, 0);
                if (_regionProbe == nint.Zero)
                {
                    return false;
                }
            }

            var currentType = NativeMethods.GetWindowRgn(Handle, _regionProbe);
            if (currentType != _lastRegionType)
            {
                return false;
            }

            var boundsType = NativeMethods.GetRgnBox(_regionProbe, out var currentBounds);
            return boundsType == _lastRegionType && currentBounds == _lastRegionBounds;
        }

        private static NativeMethods.Rect Intersect(
            NativeMethods.Rect first,
            NativeMethods.Rect second) =>
            new(
                Math.Max(first.Left, second.Left),
                Math.Max(first.Top, second.Top),
                Math.Min(first.Right, second.Right),
                Math.Min(first.Bottom, second.Bottom));

        private static void DeleteIfOwned(nint value)
        {
            if (value != nint.Zero)
            {
                _ = NativeMethods.DeleteObject(value);
            }
        }

        private static string LastWin32Error(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return error == 0
                ? message
                : $"{message}：{new Win32Exception(error).Message}（{error}）";
        }
    }

    private sealed class PortalLayer(
        PortalLayerDescriptor descriptor,
        PortalLayerForm? form)
    {
        internal PortalLayerDescriptor Descriptor { get; } = descriptor;
        internal PortalLayerForm? Form { get; } = form;
    }

    private readonly record struct PortalLayerDescriptor(
        nint Window,
        WindowCompatibilityDecision Decision);

    private readonly record struct PortalRegionSignature(
        NativeMethods.Rect SourceClip,
        IReadOnlyList<NativeMethods.Rect> OccluderClips)
    {
        public bool Equals(PortalRegionSignature other) =>
            SourceClip == other.SourceClip && OccluderClips.SequenceEqual(other.OccluderClips);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(SourceClip);
            foreach (var clip in OccluderClips)
            {
                hash.Add(clip);
            }

            return hash.ToHashCode();
        }
    }
}
