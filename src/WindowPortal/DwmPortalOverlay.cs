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

        private readonly ForegroundZOrderGuard _foregroundGuard = new();
        private readonly System.Windows.Forms.Timer _zOrderGuardTimer;
        private PortalScene? _scene;
        private NativeMethods.Point? _lastScreenCenter;
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
            if (descriptors.Count == 0)
            {
                error = "圆洞范围内没有可参与合成的后台应用窗口。";
                HidePortal();
                return false;
            }

            if (!PortalScene.TryCreate(descriptors, radius, out var scene, out error) ||
                !scene.TryPrepare(screenCenter, radius, out error) ||
                !TrySwapScenes(null, scene, screenCenter, radius, out error))
            {
                scene?.Dispose();
                HidePortal();
                return false;
            }

            _scene = scene;
            _portalVisible = true;
            _lastScreenCenter = screenCenter;
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
            _ = _foregroundGuard.TryTakePromotedWindow(
                out _,
                out var promotionError);
            if (promotionError is not null)
            {
                error = promotionError;
                return false;
            }

            if (!_portalVisible ||
                _scene is null ||
                !NativeMethods.IsWindow(_protectedWindow))
            {
                error = "视觉穿透合成器已经不可用。";
                return false;
            }

            var descriptors = DiscoverLayers(_protectedWindow, screenCenter, radius);
            if (descriptors.Count == 0)
            {
                error = "圆洞范围内没有可参与合成的后台应用窗口。";
                return false;
            }

            if (!_scene.Matches(descriptors))
            {
                if (!PortalScene.TryCreate(descriptors, radius, out var nextScene, out error) ||
                    !nextScene.TryPrepare(screenCenter, radius, out error) ||
                    !TrySwapScenes(_scene, nextScene, screenCenter, radius, out error))
                {
                    nextScene?.Dispose();
                    return false;
                }

                var previousScene = _scene;
                _scene = nextScene;
                _lastScreenCenter = screenCenter;
                _ = NativeMethods.DwmFlush();
                previousScene.Dispose();
                error = null;
                return true;
            }

            if (_lastScreenCenter == screenCenter && !_scene.SourceGeometryChanged())
            {
                error = null;
                return true;
            }

            if (!_scene.TryPrepare(screenCenter, radius, out error) ||
                !TryMoveScene(_scene, screenCenter, radius, out error))
            {
                return false;
            }

            _lastScreenCenter = screenCenter;
            _ = NativeMethods.DwmFlush();
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

            var diameter = checked((radius * 2) + 1);
            foreach (var form in forms)
            {
                position = NativeMethods.DeferWindowPos(
                    position,
                    form.Handle,
                    nint.Zero,
                    center.X - radius,
                    center.Y - radius,
                    diameter,
                    diameter,
                    NativeMethods.SwpNoZOrder |
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

                if (!form.TryPrepare(
                        sourceRects[index],
                        sourceRects.Take(index).ToArray(),
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
        private const int WsExNoActivate = 0x08000000;
        private const int WmNcHitTest = 0x0084;
        private const int WmMouseActivate = 0x0021;
        private static readonly nint HtTransparent = new(-1);
        private static readonly nint MaNoActivate = new(3);
        private static readonly Color PortalTransparencyColor = Color.FromArgb(255, 1, 0, 255);

        private readonly int _diameter;
        private nint _thumbnail;
        private PortalRegionSignature? _lastRegionSignature;

        internal PortalLayerForm(int radius)
        {
            _diameter = checked((radius * 2) + 1);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = PortalTransparencyColor;
            TransparencyKey = PortalTransparencyColor;
            TopMost = true;
            Bounds = new Rectangle(0, 0, _diameter, _diameter);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                return parameters;
            }
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
            if (_lastRegionSignature is null || !_lastRegionSignature.Value.Equals(signature))
            {
                if (!TryApplyRegion(destination, occluderClips, out error))
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

            base.Dispose(disposing);
        }

        private bool TryApplyRegion(
            NativeMethods.Rect sourceClip,
            IReadOnlyList<NativeMethods.Rect> occluderClips,
            out string? error)
        {
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

            if (NativeMethods.SetWindowRgn(Handle, region, redraw: false) == 0)
            {
                _ = NativeMethods.DeleteObject(region);
                error = LastWin32Error("无法提交 DWM 图层的圆形区域");
                return false;
            }

            // SetWindowRgn succeeded and now owns the region handle.
            error = null;
            return true;
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
