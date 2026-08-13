using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowPortal;

/// <summary>
/// Experimental single-layer GPU portal:
/// HWND WGC -> persistent D3D11 texture -> shape/feather pixel shader ->
/// DirectComposition swap chain. Capture and cursor cropping are decoupled so a
/// static source frame can still move with the pointer without another CPU grab.
/// </summary>
internal sealed class GpuPortalOverlay : IDisposable
{
    private const string ShaderSource = """
        cbuffer PortalParameters : register(b0)
        {
            float4 SourceData; // origin x/y, texture width/height
            float4 OutputData; // output width/height, shape (0 circle), feather
            float4 ShapeData;  // corner radius, circle radius, unused, unused
        };

        Texture2D<float4> SourceTexture : register(t0);
        SamplerState PointSampler : register(s0);

        float4 VSMain(uint vertexId : SV_VertexID) : SV_POSITION
        {
            float2 corner = float2((vertexId << 1) & 2, vertexId & 2);
            return float4(corner.x * 2.0 - 1.0, 1.0 - corner.y * 2.0, 0.0, 1.0);
        }

        float RoundedRectangleDistance(float2 pixel, float2 outputSize, float radius)
        {
            float2 center = (outputSize - 1.0) * 0.5;
            float2 straightHalfExtent = center - radius;
            float2 q = abs(pixel - center) - straightHalfExtent;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }

        float4 PSMain(float4 position : SV_POSITION) : SV_TARGET
        {
            float2 pixel = position.xy - 0.5;
            float2 sourcePixel = SourceData.xy + pixel;
            if (sourcePixel.x < 0.0 || sourcePixel.y < 0.0 ||
                sourcePixel.x >= SourceData.z || sourcePixel.y >= SourceData.w)
            {
                return 0.0;
            }

            float2 uv = (sourcePixel + 0.5) / SourceData.zw;
            float4 color = SourceTexture.SampleLevel(PointSampler, uv, 0.0);
            float signedDistance;
            if (OutputData.z < 0.5)
            {
                signedDistance = length(pixel - ShapeData.yy) - ShapeData.y;
            }
            else
            {
                signedDistance = RoundedRectangleDistance(
                    pixel,
                    OutputData.xy,
                    ShapeData.x);
            }

            float alpha = OutputData.w <= 0.0
                ? (signedDistance <= 0.0 ? 1.0 : 0.0)
                : saturate(-signedDistance / OutputData.w);
            color.rgb *= alpha;
            color.a *= alpha;
            return color;
        }
        """;

    private static readonly Guid Direct3DDxgiInterfaceAccessId =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly object gpuLock = new();
    private readonly PortalGeometry geometry;
    private readonly GpuDeviceResources resources;
    private readonly PortalGpuForm form;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11Texture2D backBuffer;
    private readonly ID3D11RenderTargetView renderTarget;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11Buffer parameterBuffer;
    private readonly IDCompositionTarget compositionTarget;
    private readonly IDCompositionVisual compositionVisual;
    private readonly ForegroundZOrderGuard foregroundGuard = new();

    private Direct3D11CaptureFramePool? framePool;
    private GraphicsCaptureSession? session;
    private GraphicsCaptureItem? captureItem;
    private ID3D11Texture2D? latestTexture;
    private ID3D11ShaderResourceView? latestTextureView;
    private NativeMethods.Point? lastPresentedCenter;
    private long latestCaptureSerial;
    private long presentedCaptureSerial;
    private long presentedFrames;
    private bool active;
    private bool windowShown;
    private bool disposed;
    private string? captureFailure;

    internal GpuPortalOverlay(PortalGeometry geometry)
    {
        this.geometry = geometry;
        _ = Application.OleRequired();
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture 在当前系统不可用。");
        }

        resources = GpuDeviceResources.Create();
        form = new PortalGpuForm(geometry.FrameWidth, geometry.FrameHeight);
        _ = form.Handle;

        using var adapter = resources.DxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var swapChainDescription = new SwapChainDescription1
        {
            Width = checked((uint)geometry.FrameWidth),
            Height = checked((uint)geometry.FrameHeight),
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };
        swapChain = factory.CreateSwapChainForComposition(
            resources.Device,
            swapChainDescription,
            null!);
        using (var swapChain2 = swapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
        {
            if (swapChain2 is not null)
            {
                swapChain2.MaximumFrameLatency = 1;
            }
        }

        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = resources.Device.CreateRenderTargetView(backBuffer, null);
        var vertexBytecode = GpuShaderCompiler.Compile(
            ShaderSource,
            "VSMain",
            "vs_5_0");
        var pixelBytecode = GpuShaderCompiler.Compile(
            ShaderSource,
            "PSMain",
            "ps_5_0");
        vertexShader = resources.Device.CreateVertexShader(vertexBytecode, null!);
        pixelShader = resources.Device.CreatePixelShader(pixelBytecode, null!);
        sampler = resources.Device.CreateSamplerState(SamplerDescription.PointClamp);
        var bufferDescription = new BufferDescription(
            checked((uint)Marshal.SizeOf<PortalShaderParameters>()),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
        parameterBuffer = resources.Device.CreateBuffer(
            in bufferDescription,
            nint.Zero);

        resources.CompositionDevice.CreateTargetForHwnd(
                form.Handle,
                true,
                out compositionTarget)
            .CheckError();
        compositionVisual = resources.CompositionDevice.CreateVisual();
        compositionVisual.SetContent(swapChain).CheckError();
        compositionTarget.SetRoot(compositionVisual).CheckError();
        resources.CompositionDevice.Commit().CheckError();
    }

    internal bool IsVisible => active;

    internal nint SourceWindow { get; private set; }

    internal int ForegroundRecoveryCount => foregroundGuard.RecoveryCount;

    internal int BackgroundPromotionCount => foregroundGuard.PromotionCount;

    internal long CapturedFrames => Interlocked.Read(ref latestCaptureSerial);

    internal long PresentedFrames => Interlocked.Read(ref presentedFrames);

    internal NativeMethods.Point? LastPresentedCenter => lastPresentedCenter;

    internal bool TryShow(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point screenCenter,
        out string? error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Hide();
        if (sourceWindow == nint.Zero || !NativeMethods.IsWindow(sourceWindow))
        {
            error = "透视区域下方没有可用于 GPU 捕获的窗口。";
            return false;
        }

        if (!foregroundGuard.TryEnable(
                sourceWindow,
                protectedWindow,
                screenCenter,
                geometry.GuardRadius,
                out error))
        {
            return false;
        }

        try
        {
            captureItem = GraphicsCaptureInterop.CreateForWindow(sourceWindow);
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                resources.CaptureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                captureItem.Size);
            session = framePool.CreateCaptureSession(captureItem);
            session.IsCursorCaptureEnabled = false;
            framePool.FrameArrived += OnFrameArrived;
            SourceWindow = sourceWindow;
            active = true;
            lastPresentedCenter = null;
            latestCaptureSerial = 0;
            presentedCaptureSerial = 0;
            presentedFrames = 0;
            captureFailure = null;
            session.StartCapture();
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            Hide();
            error = $"GPU 视觉捕获启动失败：{exception.Message}";
            return false;
        }
    }

    internal bool TryUpdate(
        NativeMethods.Point screenCenter,
        out string? error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // WGC capture startup and the hidden DirectComposition HWND both originate
        // on this STA thread. Pump its queue without introducing a second visual
        // thread so FreeThreaded frame callbacks can begin immediately.
        Application.DoEvents();
        if (!active || SourceWindow == nint.Zero)
        {
            error = "GPU 透视视觉源尚未启动。";
            return false;
        }

        foregroundGuard.UpdatePortalGeometry(screenCenter, geometry.GuardRadius);
        foregroundGuard.EnsurePreserved();
        if (Volatile.Read(ref captureFailure) is { } failure)
        {
            error = failure;
            return false;
        }

        if (!TryGetCaptureBounds(SourceWindow, out var sourceBounds))
        {
            error = "无法读取 GPU 捕获窗口的位置。";
            return false;
        }

        lock (gpuLock)
        {
            var texture = latestTexture;
            var textureView = latestTextureView;
            if (texture is null || textureView is null)
            {
                error = null;
                return true;
            }

            var captureSerial = Interlocked.Read(ref latestCaptureSerial);
            if (lastPresentedCenter == screenCenter &&
                presentedCaptureSerial == captureSerial)
            {
                error = null;
                return true;
            }

            var frameBounds = geometry.CreateFrameBounds(screenCenter);
            var parameters = new PortalShaderParameters(
                new Vector4(
                    frameBounds.Left - sourceBounds.Left,
                    frameBounds.Top - sourceBounds.Top,
                    texture.Description.Width,
                    texture.Description.Height),
                new Vector4(
                    geometry.FrameWidth,
                    geometry.FrameHeight,
                    geometry.Shape == PortalShape.Circle ? 0f : 1f,
                    geometry.EffectiveFeatherWidth),
                new Vector4(
                    geometry.EffectiveCornerRadius,
                    geometry.Radius,
                    0f,
                    0f));
            resources.Context.UpdateSubresource(
                in parameters,
                parameterBuffer,
                0,
                0,
                0,
                null);
            resources.Context.OMSetRenderTargets(renderTarget, null!);
            resources.Context.RSSetViewport(
                0,
                0,
                geometry.FrameWidth,
                geometry.FrameHeight,
                0,
                1);
            resources.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            resources.Context.VSSetShader(vertexShader);
            resources.Context.PSSetShader(pixelShader);
            resources.Context.PSSetShaderResource(0, textureView);
            resources.Context.PSSetSampler(0, sampler);
            resources.Context.PSSetConstantBuffer(0, parameterBuffer);
            resources.Context.Draw(3, 0);
            resources.Context.PSUnsetShaderResource(0);
            resources.Context.UnsetRenderTargets();
            swapChain.Present(0, PresentFlags.None).CheckError();
            Interlocked.Increment(ref presentedFrames);

            if (!NativeMethods.SetWindowPos(
                    form.Handle,
                    NativeMethods.HwndTopMost,
                    frameBounds.Left,
                    frameBounds.Top,
                    geometry.FrameWidth,
                    geometry.FrameHeight,
                    NativeMethods.SwpNoActivate |
                    NativeMethods.SwpNoOwnerZOrder |
                    NativeMethods.SwpShowWindow))
            {
                error = "无法移动 GPU 透视窗口。";
                return false;
            }

            windowShown = true;
            lastPresentedCenter = screenCenter;
            presentedCaptureSerial = captureSerial;
        }

        error = null;
        return true;
    }

    internal void Hide()
    {
        if (disposed)
        {
            return;
        }

        active = false;
        SourceWindow = nint.Zero;
        foregroundGuard.Restore();
        if (windowShown && form.IsHandleCreated)
        {
            _ = NativeMethods.SetWindowPos(
                form.Handle,
                nint.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpHideWindow);
        }

        windowShown = false;
        var oldPool = framePool;
        framePool = null;
        if (oldPool is not null)
        {
            oldPool.FrameArrived -= OnFrameArrived;
        }

        session?.Dispose();
        session = null;
        oldPool?.Dispose();
        captureItem = null;
        lock (gpuLock)
        {
            latestTextureView?.Dispose();
            latestTextureView = null;
            latestTexture?.Dispose();
            latestTexture = null;
            lastPresentedCenter = null;
            latestCaptureSerial = 0;
            presentedCaptureSerial = 0;
            presentedFrames = 0;
            captureFailure = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Hide();
        disposed = true;
        parameterBuffer.Dispose();
        sampler.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
        renderTarget.Dispose();
        backBuffer.Dispose();
        compositionVisual.Dispose();
        compositionTarget.Dispose();
        swapChain.Dispose();
        form.Dispose();
        resources.Dispose();
    }

    private void OnFrameArrived(
        Direct3D11CaptureFramePool sender,
        object arguments)
    {
        try
        {
            _ = arguments;
            using var frame = sender.TryGetNextFrame();
            if (frame is null || !active || !ReferenceEquals(sender, framePool))
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
                WinRT.MarshalInterface<IDirect3DSurface>.DisposeAbi(surfacePointer);
            }

            using var access = new IDirect3DDxgiInterfaceAccess(accessPointer);
            using var sourceTexture = access.GetInterface<ID3D11Texture2D>();
            lock (gpuLock)
            {
                if (!active || !ReferenceEquals(sender, framePool))
                {
                    return;
                }

                var description = sourceTexture.Description;
                if (latestTexture is null ||
                    latestTexture.Description.Width != description.Width ||
                    latestTexture.Description.Height != description.Height)
                {
                    latestTextureView?.Dispose();
                    latestTexture?.Dispose();
                    var persistentDescription = new Texture2DDescription(
                        Format.B8G8R8A8_UNorm,
                        description.Width,
                        description.Height,
                        1,
                        1,
                        BindFlags.ShaderResource,
                        ResourceUsage.Default,
                        CpuAccessFlags.None,
                        1,
                        0,
                        ResourceOptionFlags.None);
                    latestTexture = resources.Device.CreateTexture2D(
                        persistentDescription);
                    latestTextureView = resources.Device.CreateShaderResourceView(
                        latestTexture,
                        null);
                }

                resources.Context.CopyResource(latestTexture, sourceTexture);
                Interlocked.Increment(ref latestCaptureSerial);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(
                ref captureFailure,
                $"GPU 捕获帧处理失败：{exception.Message}");
        }
    }

    private static bool TryGetCaptureBounds(
        nint window,
        out NativeMethods.Rect bounds)
    {
        var extendedFrameResult = NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaExtendedFrameBounds,
            out bounds,
            Marshal.SizeOf<NativeMethods.Rect>());
        if (extendedFrameResult == 0 && bounds.Width > 1 && bounds.Height > 1)
        {
            return true;
        }

        return NativeMethods.GetWindowRect(window, out bounds) &&
               bounds.Width > 1 &&
               bounds.Height > 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PortalShaderParameters(
        Vector4 SourceData,
        Vector4 OutputData,
        Vector4 ShapeData);

    private sealed class PortalGpuForm : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExNoRedirectionBitmap = 0x00200000;

        internal PortalGpuForm(int width, int height)
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = System.Drawing.Color.Black;
            ClientSize = new System.Drawing.Size(width, height);
            FormBorderStyle = FormBorderStyle.None;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
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
}
