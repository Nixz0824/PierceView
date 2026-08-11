using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowPortal;

internal static class GpuCaptureSmokeTests
{
    private const int DefaultWidth = 640;
    private const int DefaultHeight = 400;
    private static readonly Guid Direct3DDxgiInterfaceAccessId =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    internal static int Run(nint sourceWindow, int durationMilliseconds)
    {
        _ = Application.OleRequired();
        if (!GraphicsCaptureSession.IsSupported())
        {
            Console.Error.WriteLine("Windows.Graphics.Capture 不可用。");
            return 12;
        }

        try
        {
            var item = GraphicsCaptureInterop.CreateForWindow(sourceWindow);
            var width = Math.Clamp(item.Size.Width, 1, DefaultWidth);
            var height = Math.Clamp(item.Size.Height, 1, DefaultHeight);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new GpuSmokeForm(width, height);
            _ = form.Handle;
            using var renderer = new GpuCaptureSmokeRenderer(form.Handle, item, width, height);

            var timer = new System.Windows.Forms.Timer
            {
                Interval = Math.Clamp(durationMilliseconds, 100, 60000),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                form.Close();
            };

            form.Shown += (_, _) =>
            {
                renderer.Start();
                timer.Start();
            };

            Console.WriteLine(
                $"GPU 闭环测试：来源 HWND=0x{sourceWindow:X}，" +
                $"矩形={width}×{height}，持续={durationMilliseconds}ms。");
            Application.Run(form);
            timer.Dispose();

            var statistics = renderer.Statistics;
            Console.WriteLine(
                $"WGC 帧={statistics.CapturedFrames}，GPU 提交={statistics.PresentedFrames}，" +
                $"忙时丢弃={statistics.BusyDrops}，平均提交={statistics.FramesPerSecond:F1}fps，" +
                $"最大帧间隔={statistics.MaximumFrameIntervalMilliseconds:F2}ms。");

            if (statistics.PresentedFrames == 0)
            {
                Console.Error.WriteLine("WGC 未产生可提交的 GPU 帧。");
                return 13;
            }

            Console.WriteLine("HWND WGC → D3D11 纹理裁剪 → DirectComposition 闭环通过。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"GPU 闭环测试失败：HRESULT=0x{exception.HResult:X8}，" +
                $"{exception.GetType().Name}：{exception.Message}");
            return 14;
        }
    }

    private sealed class GpuCaptureSmokeRenderer : IDisposable
    {
        private readonly GpuDeviceResources resources;
        private readonly GraphicsCaptureItem item;
        private readonly Direct3D11CaptureFramePool framePool;
        private readonly GraphicsCaptureSession session;
        private readonly IDXGISwapChain1 swapChain;
        private readonly ID3D11Texture2D backBuffer;
        private readonly IDCompositionTarget target;
        private readonly IDCompositionVisual visual;
        private readonly int width;
        private readonly int height;
        private readonly Stopwatch stopwatch = new();
        private long lastFrameTimestamp;
        private double maximumFrameIntervalMilliseconds;
        private long capturedFrames;
        private long presentedFrames;
        private long busyDrops;
        private int rendering;
        private bool started;
        private bool disposed;

        internal GpuCaptureSmokeRenderer(
            nint destinationWindow,
            GraphicsCaptureItem item,
            int width,
            int height)
        {
            this.width = width;
            this.height = height;
            this.item = item;
            resources = GpuDeviceResources.Create();

            using var adapter = resources.DxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var description = new SwapChainDescription1
            {
                Width = checked((uint)width),
                Height = checked((uint)height),
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            swapChain = factory.CreateSwapChainForComposition(
                resources.Device,
                description,
                null!);
            backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);

            var targetResult = resources.CompositionDevice.CreateTargetForHwnd(
                destinationWindow,
                true,
                out target);
            targetResult.CheckError();
            visual = resources.CompositionDevice.CreateVisual();
            visual.SetContent(swapChain).CheckError();
            target.SetRoot(visual).CheckError();
            resources.CompositionDevice.Commit().CheckError();

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                resources.CaptureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            framePool.FrameArrived += OnFrameArrived;
        }

        internal CaptureStatistics Statistics
        {
            get
            {
                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                return new CaptureStatistics(
                    Interlocked.Read(ref capturedFrames),
                    Interlocked.Read(ref presentedFrames),
                    Interlocked.Read(ref busyDrops),
                    elapsedSeconds <= 0
                        ? 0
                        : Interlocked.Read(ref presentedFrames) / elapsedSeconds,
                    maximumFrameIntervalMilliseconds);
            }
        }

        internal void Start()
        {
            if (started)
            {
                return;
            }

            started = true;
            stopwatch.Start();
            session.StartCapture();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            framePool.FrameArrived -= OnFrameArrived;
            session.Dispose();
            framePool.Dispose();
            GC.KeepAlive(item);
            resources.Context.Flush();
            visual.Dispose();
            target.Dispose();
            backBuffer.Dispose();
            swapChain.Dispose();
            resources.Dispose();
        }

        private void OnFrameArrived(
            Direct3D11CaptureFramePool sender,
            object arguments)
        {
            _ = arguments;
            Interlocked.Increment(ref capturedFrames);
            if (Interlocked.Exchange(ref rendering, 1) != 0)
            {
                Interlocked.Increment(ref busyDrops);
                return;
            }

            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null || disposed)
                {
                    return;
                }

                var surfacePointer =
                    WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
                nint accessPointer = nint.Zero;
                try
                {
                    var accessInterfaceId = Direct3DDxgiInterfaceAccessId;
                    var queryResult = Marshal.QueryInterface(
                        surfacePointer,
                        ref accessInterfaceId,
                        out accessPointer);
                    Marshal.ThrowExceptionForHR(queryResult);
                }
                finally
                {
                    WinRT.MarshalInterface<IDirect3DSurface>.DisposeAbi(
                        surfacePointer);
                }

                using var access = new IDirect3DDxgiInterfaceAccess(accessPointer);
                using var sourceTexture = access.GetInterface<ID3D11Texture2D>();
                var sourceDescription = sourceTexture.Description;
                var copyWidth = Math.Min(width, checked((int)sourceDescription.Width));
                var copyHeight = Math.Min(height, checked((int)sourceDescription.Height));
                var sourceLeft = Math.Max(
                    0,
                    (checked((int)sourceDescription.Width) - copyWidth) / 2);
                var sourceTop = Math.Max(
                    0,
                    (checked((int)sourceDescription.Height) - copyHeight) / 2);
                var sourceRegion = new Box(
                    sourceLeft,
                    sourceTop,
                    0,
                    sourceLeft + copyWidth,
                    sourceTop + copyHeight,
                    1);

                resources.Context.CopySubresourceRegion(
                    backBuffer,
                    0,
                    0,
                    0,
                    0,
                    sourceTexture,
                    0,
                    sourceRegion);
                swapChain.Present(0, PresentFlags.None).CheckError();
                RecordPresentation();
            }
            catch (ObjectDisposedException)
            {
                // The test window can close while a free-threaded WGC callback is in flight.
            }
            finally
            {
                Volatile.Write(ref rendering, 0);
            }
        }

        private void RecordPresentation()
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref lastFrameTimestamp, now);
            if (previous != 0)
            {
                var interval = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                if (interval > maximumFrameIntervalMilliseconds)
                {
                    maximumFrameIntervalMilliseconds = interval;
                }
            }

            Interlocked.Increment(ref presentedFrames);
        }
    }

    private sealed class GpuSmokeForm : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExNoRedirectionBitmap = 0x00200000;

        internal GpuSmokeForm(int width, int height)
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = System.Drawing.Color.Black;
            ClientSize = new System.Drawing.Size(width, height);
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            var screen = Screen.FromPoint(Cursor.Position);
            Location = new Point(
                screen.WorkingArea.Left +
                    Math.Max(0, (screen.WorkingArea.Width - width) / 2),
                screen.WorkingArea.Top +
                    Math.Max(0, (screen.WorkingArea.Height - height) / 2));
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
                    WsExNoActivate |
                    WsExNoRedirectionBitmap;
                return parameters;
            }
        }
    }

    internal readonly record struct CaptureStatistics(
        long CapturedFrames,
        long PresentedFrames,
        long BusyDrops,
        double FramesPerSecond,
        double MaximumFrameIntervalMilliseconds);
}
