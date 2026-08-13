using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowPortal;

/// <summary>
/// GPU portal compositor with one to four HWND WGC sources:
/// WGC -> persistent D3D11 textures -> Z-order occlusion/shape/feather pixel
/// shader -> one DirectComposition swap chain. Capture and cursor cropping are
/// decoupled so static source frames can move without another CPU grab.
/// </summary>
internal sealed class GpuPortalOverlay : IDisposable
{
    private const int MaximumCanvasDimension = 16_384;
    private const int OrderSwitchMaximumHoldMilliseconds = 8;
    private const int SourceReconciliationMilliseconds = 75;

    private const string ShaderSource = """
        cbuffer PortalParameters : register(b0)
        {
            float4 SourceData; // origin x/y, texture width/height
            float4 OutputData; // canvas width/height, shape (0 circle), feather
            float4 ShapeData;  // corner radius, circle radius, source count, unused
            float4 PortalData; // portal width/height, local center x/y
            float4 SourceData1;
            float4 SourceData2;
            float4 SourceData3;
        };

        Texture2D<float4> SourceTexture0 : register(t0);
        Texture2D<float4> SourceTexture1 : register(t1);
        Texture2D<float4> SourceTexture2 : register(t2);
        Texture2D<float4> SourceTexture3 : register(t3);
        SamplerState PointSampler : register(s0);

        float4 VSMain(uint vertexId : SV_VertexID) : SV_POSITION
        {
            float2 corner = float2((vertexId << 1) & 2, vertexId & 2);
            return float4(corner.x * 2.0 - 1.0, 1.0 - corner.y * 2.0, 0.0, 1.0);
        }

        float RoundedRectangleDistance(
            float2 pixel,
            float2 center,
            float2 portalSize,
            float radius)
        {
            float2 halfExtent = (portalSize - 1.0) * 0.5;
            float2 straightHalfExtent = max(halfExtent - radius, 0.0);
            float2 q = abs(pixel - center) - straightHalfExtent;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }

        float4 PSMain(float4 position : SV_POSITION) : SV_TARGET
        {
            float2 pixel = position.xy - 0.5;
            float4 color = 0.0;
            float2 sourcePixel;
            float4 sampled;
            if (ShapeData.z >= 4.0)
            {
                sourcePixel = SourceData3.xy + pixel;
                if (sourcePixel.x >= 0.0 && sourcePixel.y >= 0.0 &&
                    sourcePixel.x < SourceData3.z && sourcePixel.y < SourceData3.w)
                {
                    float2 uv = (sourcePixel + 0.5) / SourceData3.zw;
                    color = SourceTexture3.SampleLevel(PointSampler, uv, 0.0);
                }
            }
            if (ShapeData.z >= 3.0)
            {
                sourcePixel = SourceData2.xy + pixel;
                if (sourcePixel.x >= 0.0 && sourcePixel.y >= 0.0 &&
                    sourcePixel.x < SourceData2.z && sourcePixel.y < SourceData2.w)
                {
                    float2 uv = (sourcePixel + 0.5) / SourceData2.zw;
                    sampled = SourceTexture2.SampleLevel(PointSampler, uv, 0.0);
                    color = sampled + color * (1.0 - sampled.a);
                }
            }
            if (ShapeData.z >= 2.0)
            {
                sourcePixel = SourceData1.xy + pixel;
                if (sourcePixel.x >= 0.0 && sourcePixel.y >= 0.0 &&
                    sourcePixel.x < SourceData1.z && sourcePixel.y < SourceData1.w)
                {
                    float2 uv = (sourcePixel + 0.5) / SourceData1.zw;
                    sampled = SourceTexture1.SampleLevel(PointSampler, uv, 0.0);
                    color = sampled + color * (1.0 - sampled.a);
                }
            }
            if (ShapeData.z >= 1.0)
            {
                sourcePixel = SourceData.xy + pixel;
                if (sourcePixel.x >= 0.0 && sourcePixel.y >= 0.0 &&
                    sourcePixel.x < SourceData.z && sourcePixel.y < SourceData.w)
                {
                    float2 uv = (sourcePixel + 0.5) / SourceData.zw;
                    sampled = SourceTexture0.SampleLevel(PointSampler, uv, 0.0);
                    color = sampled + color * (1.0 - sampled.a);
                }
            }
            float signedDistance;
            if (OutputData.z < 0.5)
            {
                signedDistance = length(pixel - PortalData.zw) - ShapeData.y;
            }
            else
            {
                signedDistance = RoundedRectangleDistance(
                    pixel,
                    PortalData.zw,
                    PortalData.xy,
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
    private readonly NativeMethods.Rect canvasBounds;
    private readonly int canvasWidth;
    private readonly int canvasHeight;
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
    private readonly WindowOwnerGuard ownerGuard = new();
    private readonly System.Threading.Timer foregroundHeartbeat;

    private readonly List<CaptureSource> captureSources = [];
    private readonly Dictionary<Direct3D11CaptureFramePool, CaptureSource>
        captureSourceByPool = [];
    private NativeMethods.Point? lastPresentedCenter;
    private NativeMethods.Rect[]? lastPresentedSourceBounds;
    private long latestCaptureSerial;
    private long presentedCaptureSerial;
    private long presentedFrames;
    private long compositionOrderSerial;
    private long presentedCompositionOrderSerial;
    private long pendingOrderCaptureSerial;
    private nint pendingOrderSourceWindow;
    private long pendingOrderSourceFrameSerial;
    private long pendingOrderDeadlineTimestamp;
    private long nextSourceReconciliationTimestamp;
    private int displayPlacementCount;
    private int sourceReconciliationCount;
    private int sourceReplacementCount;
    private nint protectedWindow;
    private volatile bool active;
    private bool windowShown;
    private bool disposed;
    private int recoverableCaptureFailureCount;
    private int recoverableUpdateFailureCount;
    private int sourceReconciliationRetryCount;

    internal GpuPortalOverlay(PortalGeometry geometry)
    {
        this.geometry = geometry;
        canvasBounds = CreateVirtualCanvasBounds(SystemInformation.VirtualScreen);
        canvasWidth = canvasBounds.Width;
        canvasHeight = canvasBounds.Height;
        if (canvasWidth > MaximumCanvasDimension ||
            canvasHeight > MaximumCanvasDimension)
        {
            throw new PlatformNotSupportedException(
                $"Windows 虚拟屏幕 {canvasWidth}x{canvasHeight} 超出 GPU 画布上限 {MaximumCanvasDimension}。" );
        }
        _ = Application.OleRequired();
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture 在当前系统不可用。");
        }

        resources = GpuDeviceResources.Create();
        foregroundHeartbeat = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    if (active)
                    {
                        foregroundGuard.EnsurePreserved();
                    }
                }
                catch
                {
                    // Foreground recovery must never terminate the timer thread.
                }
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        form = new PortalGpuForm(canvasWidth, canvasHeight);
        _ = form.Handle;

        using var adapter = resources.DxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var swapChainDescription = new SwapChainDescription1
        {
            Width = checked((uint)canvasWidth),
            Height = checked((uint)canvasHeight),
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
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

        // Flip-model D3D11 swap chains expose the current renderable surface as
        // buffer 0. Present unbinds it, so every update explicitly rebinds this
        // RTV before drawing the next frame.
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

    internal int SourceCount
    {
        get
        {
            lock (gpuLock)
            {
                return captureSources.Count;
            }
        }
    }

    internal int ForegroundRecoveryCount => foregroundGuard.RecoveryCount;

    internal int ImmediateForegroundClampCount =>
        foregroundGuard.ImmediateClampCount;

    internal int BackgroundPromotionCount => foregroundGuard.PromotionCount;

    internal int PhysicalOrderRecoveryCount =>
        foregroundGuard.PhysicalOrderRecoveryCount;

    internal nint PhysicallySelectedSourceWindow =>
        foregroundGuard.SelectedSourceWindow;

    internal bool IsPhysicalSourceOrderSynchronized =>
        foregroundGuard.IsSelectedSourceFrontmost;

    internal static bool ShouldHoldCompositionOrderSwitch(
        long presentedOrderSerial,
        long currentOrderSerial,
        long captureSerial,
        long orderStartCaptureSerial,
        long currentPromotedSourceFrameSerial,
        long orderStartPromotedSourceFrameSerial,
        bool holdDeadlineExpired) =>
        presentedOrderSerial != currentOrderSerial &&
        !holdDeadlineExpired &&
        (captureSerial <= orderStartCaptureSerial ||
         currentPromotedSourceFrameSerial <=
         orderStartPromotedSourceFrameSerial);

    internal long CapturedFrames => Interlocked.Read(ref latestCaptureSerial);

    internal long PresentedFrames => Interlocked.Read(ref presentedFrames);

    internal NativeMethods.Point? LastPresentedCenter => lastPresentedCenter;

    internal int DisplayPlacementCount => Volatile.Read(ref displayPlacementCount);

    internal int SourceReconciliationCount =>
        Volatile.Read(ref sourceReconciliationCount);

    internal int SourceReplacementCount =>
        Volatile.Read(ref sourceReplacementCount);

    internal int RecoverableCaptureFailureCount =>
        Volatile.Read(ref recoverableCaptureFailureCount);

    internal int RecoverableUpdateFailureCount =>
        Volatile.Read(ref recoverableUpdateFailureCount);

    internal int SourceReconciliationRetryCount =>
        Volatile.Read(ref sourceReconciliationRetryCount);

    internal IReadOnlyList<nint> SourceWindows
    {
        get
        {
            lock (gpuLock)
            {
                return captureSources.Select(source => source.Window).ToArray();
            }
        }
    }

    internal NativeMethods.Rect DisplayBounds => canvasBounds;

    internal bool HasInputPassThrough => form.HasInputPassThrough;

    internal bool IsSkippedBySystemHitTestAt(
        NativeMethods.Point screenPoint)
    {
        if (!windowShown)
        {
            return false;
        }

        var hitWindow = NativeMethods.WindowFromPoint(screenPoint);
        var hitRoot = hitWindow == nint.Zero
            ? nint.Zero
            : NativeMethods.GetAncestor(hitWindow, NativeMethods.GaRoot);
        return hitWindow != form.Handle && hitRoot != form.Handle;
    }

    internal bool TryShow(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point screenCenter,
        out string? error)
    {
        if (!MultilayerWindowResolver.TryGetWindowBounds(
                sourceWindow,
                out var sourceBounds))
        {
            sourceBounds = default;
        }

        return TryShow(
            [new MultilayerWindowSource(sourceWindow, sourceBounds)],
            protectedWindow,
            screenCenter,
            out error);
    }

    internal bool TryShow(
        IReadOnlyList<MultilayerWindowSource> sources,
        nint protectedWindow,
        NativeMethods.Point screenCenter,
        out string? error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Hide();
        if (sources.Count == 0 ||
            sources.Count > MultilayerWindowResolver.MaximumLayerCount ||
            sources.Any(source =>
                source.Handle == nint.Zero ||
                !NativeMethods.IsWindow(source.Handle)) ||
            sources.Select(source => source.Handle).Distinct().Count() != sources.Count)
        {
            error = "透视区域下方没有 1 到 4 个有效且不重复的 GPU 捕获窗口。";
            return false;
        }

        if (!ownerGuard.TryEnable(form.Handle, protectedWindow, out error))
        {
            return false;
        }

        if (!foregroundGuard.TryEnable(
                sources.Select(source => source.Handle).ToArray(),
                protectedWindow,
                screenCenter,
                geometry.GuardRadius,
                out error))
        {
            ownerGuard.Restore();
            return false;
        }

        try
        {
            foreach (var source in sources)
            {
                var captureSource = CaptureSource.Create(
                    source.Handle,
                    resources.CaptureDevice);
                captureSource.FramePool.FrameArrived += OnFrameArrived;
                captureSources.Add(captureSource);
                captureSourceByPool.Add(captureSource.FramePool, captureSource);
            }

            SourceWindow = sources[0].Handle;
            this.protectedWindow = protectedWindow;
            active = true;
            lastPresentedCenter = null;
            lastPresentedSourceBounds = null;
            latestCaptureSerial = 0;
            presentedCaptureSerial = 0;
            presentedFrames = 0;
            compositionOrderSerial = 0;
            presentedCompositionOrderSerial = 0;
            pendingOrderCaptureSerial = 0;
            pendingOrderSourceWindow = nint.Zero;
            pendingOrderSourceFrameSerial = 0;
            pendingOrderDeadlineTimestamp = 0;
            displayPlacementCount = 0;
            sourceReconciliationCount = 0;
            sourceReplacementCount = 0;
            recoverableCaptureFailureCount = 0;
            recoverableUpdateFailureCount = 0;
            sourceReconciliationRetryCount = 0;
            nextSourceReconciliationTimestamp = Stopwatch.GetTimestamp() +
                (long)(Stopwatch.Frequency *
                       (SourceReconciliationMilliseconds / 1000d));
            foreach (var source in captureSources)
            {
                source.Session.StartCapture();
            }
            _ = foregroundHeartbeat.Change(16, 16);
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
        try
        {
            return TryUpdateCore(screenCenter, out error);
        }
        catch (Exception exception)
        {
            if (active && windowShown && lastPresentedCenter is not null)
            {
                // The already-presented flip-model surface remains owned by
                // DirectComposition. Keep it visible across an isolated D3D,
                // WGC, or Win32 update exception and let the next loop retry.
                _ = exception;
                Interlocked.Increment(ref recoverableUpdateFailureCount);
                error = null;
                return true;
            }

            error = $"GPU 透视更新失败：{exception.Message}";
            return false;
        }
    }

    private bool TryUpdateCore(
        NativeMethods.Point screenCenter,
        out string? error)
    {
        // WGC capture startup and the hidden DirectComposition HWND both originate
        // on this STA thread. Pump its queue without introducing a second visual
        // thread so FreeThreaded frame callbacks can begin immediately.
        Application.DoEvents();
        if (!active || SourceWindow == nint.Zero)
        {
            error = "GPU 透视视觉源尚未启动。";
            return false;
        }

        if (!TryReconcileSources(screenCenter, out error))
        {
            return false;
        }

        foregroundGuard.UpdatePortalGeometry(screenCenter, geometry.GuardRadius);
        foregroundGuard.EnsurePreserved();
        lock (gpuLock)
        {
            if (captureSources.Count == 0)
            {
                error = "GPU 多层来源已停止。";
                return false;
            }

            var sourceBounds = new NativeMethods.Rect[captureSources.Count];
            for (var index = 0; index < captureSources.Count; index++)
            {
                var source = captureSources[index];
                if (source.Texture is null || source.TextureView is null)
                {
                    error = null;
                    return true;
                }

                if (!MultilayerWindowResolver.TryGetWindowBounds(
                        source.Window,
                        out sourceBounds[index]))
                {
                    // A source can disappear between reconciliation passes.
                    // Keep the last complete swap-chain frame until the next
                    // pass removes it and backfills a deeper eligible window.
                    error = null;
                    return true;
                }
            }

            if (captureSources.Any(source => source.Texture is null))
            {
                error = null;
                return true;
            }

            var captureSerial = Interlocked.Read(ref latestCaptureSerial);
            var orderSerial = compositionOrderSerial;
            var currentPromotedSourceFrameSerial = pendingOrderSourceWindow == nint.Zero
                ? pendingOrderSourceFrameSerial + 1
                : captureSources
                    .First(source => source.Window == pendingOrderSourceWindow)
                    .FrameSerial;
            if (ShouldHoldCompositionOrderSwitch(
                    presentedCompositionOrderSerial,
                    orderSerial,
                    captureSerial,
                    pendingOrderCaptureSerial,
                    currentPromotedSourceFrameSerial,
                    pendingOrderSourceFrameSerial,
                    holdDeadlineExpired:
                        pendingOrderDeadlineTimestamp != 0 &&
                        Stopwatch.GetTimestamp() >= pendingOrderDeadlineTimestamp))
            {
                // The native Z-order changes before WGC publishes the resulting
                // source frame. Keep the last complete composition on screen
                // instead of presenting a one-frame mixed old/new ordering.
                error = null;
                return true;
            }

            if (lastPresentedCenter == screenCenter &&
                presentedCaptureSerial == captureSerial &&
                presentedCompositionOrderSerial == orderSerial &&
                SourceBoundsEqual(lastPresentedSourceBounds, sourceBounds))
            {
                error = null;
                return true;
            }

            var sourceData = new Vector4[MultilayerWindowResolver.MaximumLayerCount];
            for (var index = 0; index < captureSources.Count; index++)
            {
                var textureDescription = captureSources[index].Texture!.Description;
                sourceData[index] = new Vector4(
                    canvasBounds.Left - sourceBounds[index].Left,
                    canvasBounds.Top - sourceBounds[index].Top,
                    textureDescription.Width,
                    textureDescription.Height);
            }

            var parameters = new PortalShaderParameters(
                sourceData[0],
                new Vector4(
                    canvasWidth,
                    canvasHeight,
                    geometry.Shape == PortalShape.Circle ? 0f : 1f,
                    geometry.EffectiveFeatherWidth),
                new Vector4(
                    geometry.EffectiveCornerRadius,
                    geometry.Radius,
                    captureSources.Count,
                    0f),
                new Vector4(
                    geometry.FrameWidth,
                    geometry.FrameHeight,
                    screenCenter.X - canvasBounds.Left,
                    screenCenter.Y - canvasBounds.Top),
                sourceData[1],
                sourceData[2],
                sourceData[3]);
            resources.Context.UpdateSubresource(
                in parameters,
                parameterBuffer,
                0,
                0,
                0,
                null);
            resources.Context.OMSetRenderTargets(renderTarget, null!);
            resources.Context.ClearRenderTargetView(
                renderTarget,
                new Color4(0f, 0f, 0f, 0f));
            var localFrameLeft = screenCenter.X - canvasBounds.Left -
                                 (geometry.FrameWidth / 2);
            var localFrameTop = screenCenter.Y - canvasBounds.Top -
                                (geometry.FrameHeight / 2);
            resources.Context.RSSetViewport(
                localFrameLeft,
                localFrameTop,
                geometry.FrameWidth,
                geometry.FrameHeight,
                0,
                1);
            resources.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            resources.Context.VSSetShader(vertexShader);
            resources.Context.PSSetShader(pixelShader);
            for (var index = 0; index < captureSources.Count; index++)
            {
                resources.Context.PSSetShaderResource(
                    checked((uint)index),
                    captureSources[index].TextureView!);
            }
            resources.Context.PSSetSampler(0, sampler);
            resources.Context.PSSetConstantBuffer(0, parameterBuffer);
            resources.Context.Draw(3, 0);
            for (var index = 0; index < captureSources.Count; index++)
            {
                resources.Context.PSUnsetShaderResource(checked((uint)index));
            }
            resources.Context.UnsetRenderTargets();
            swapChain.Present(0, PresentFlags.None).CheckError();
            Interlocked.Increment(ref presentedFrames);

            if (!windowShown &&
                !NativeMethods.SetWindowPos(
                    form.Handle,
                    NativeMethods.HwndTopMost,
                    canvasBounds.Left,
                    canvasBounds.Top,
                    canvasWidth,
                    canvasHeight,
                    NativeMethods.SwpNoActivate |
                    NativeMethods.SwpNoOwnerZOrder |
                    NativeMethods.SwpShowWindow))
            {
                error = "无法移动 GPU 透视窗口。";
                return false;
            }

            if (!windowShown)
            {
                Interlocked.Increment(ref displayPlacementCount);
            }

            windowShown = true;
            lastPresentedCenter = screenCenter;
            lastPresentedSourceBounds = sourceBounds;
            presentedCaptureSerial = captureSerial;
            presentedCompositionOrderSerial = orderSerial;
            if (pendingOrderSourceWindow != nint.Zero)
            {
                pendingOrderSourceWindow = nint.Zero;
                pendingOrderSourceFrameSerial = 0;
                pendingOrderDeadlineTimestamp = 0;
            }
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
        _ = foregroundHeartbeat.Change(Timeout.Infinite, Timeout.Infinite);
        SourceWindow = nint.Zero;
        protectedWindow = nint.Zero;
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
        foregroundGuard.Restore();
        ownerGuard.Restore();
        CaptureSource[] oldSources;
        lock (gpuLock)
        {
            oldSources = captureSources.ToArray();
            captureSources.Clear();
            captureSourceByPool.Clear();
        }

        foreach (var source in oldSources)
        {
            source.FramePool.FrameArrived -= OnFrameArrived;
            source.Dispose();
        }

        lock (gpuLock)
        {
            lastPresentedCenter = null;
            lastPresentedSourceBounds = null;
            latestCaptureSerial = 0;
            presentedCaptureSerial = 0;
            presentedFrames = 0;
            compositionOrderSerial = 0;
            presentedCompositionOrderSerial = 0;
            pendingOrderCaptureSerial = 0;
            pendingOrderSourceWindow = nint.Zero;
            pendingOrderSourceFrameSerial = 0;
            pendingOrderDeadlineTimestamp = 0;
            displayPlacementCount = 0;
            sourceReconciliationCount = 0;
            sourceReplacementCount = 0;
            recoverableCaptureFailureCount = 0;
            recoverableUpdateFailureCount = 0;
            sourceReconciliationRetryCount = 0;
            nextSourceReconciliationTimestamp = 0;
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
        foregroundHeartbeat.Dispose();
        ownerGuard.Dispose();
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
            CaptureSource? captureSource;
            lock (gpuLock)
            {
                captureSourceByPool.TryGetValue(sender, out captureSource);
            }

            if (captureSource is null)
            {
                return;
            }

            using var frame = sender.TryGetNextFrame();
            if (frame is null || !active)
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
                if (!active ||
                    !captureSourceByPool.TryGetValue(sender, out var currentSource) ||
                    !ReferenceEquals(captureSource, currentSource))
                {
                    return;
                }

                var description = sourceTexture.Description;
                if (captureSource.Texture is null ||
                    captureSource.Texture.Description.Width != description.Width ||
                    captureSource.Texture.Description.Height != description.Height)
                {
                    captureSource.TextureView?.Dispose();
                    captureSource.Texture?.Dispose();
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
                    captureSource.Texture = resources.Device.CreateTexture2D(
                        persistentDescription);
                    captureSource.TextureView = resources.Device.CreateShaderResourceView(
                        captureSource.Texture,
                        null);
                }

                resources.Context.CopyResource(captureSource.Texture, sourceTexture);
                captureSource.FrameSerial++;
                Interlocked.Increment(ref latestCaptureSerial);
            }
        }
        catch (Exception exception)
        {
            lock (gpuLock)
            {
                // WGC may publish a final callback while a window is closing,
                // resizing, or being replaced. A single source-frame failure
                // must not tear down the shared DirectComposition surface: the
                // last complete swap-chain frame remains valid and a later
                // callback or reconciliation pass can recover this source.
                if (active && captureSourceByPool.ContainsKey(sender))
                {
                    _ = exception;
                    Interlocked.Increment(ref recoverableCaptureFailureCount);
                }
            }
        }
    }

    private static bool SourceBoundsEqual(
        IReadOnlyList<NativeMethods.Rect>? first,
        IReadOnlyList<NativeMethods.Rect> second)
    {
        if (first is null || first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (first[index] != second[index])
            {
                return false;
            }
        }

        return true;
    }

    internal static NativeMethods.Rect CreateVirtualCanvasBounds(
        System.Drawing.Rectangle virtualScreen)
    {
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualScreen),
                "Windows 虚拟屏幕尺寸必须为正数。" );
        }

        return new NativeMethods.Rect(
            virtualScreen.Left,
            virtualScreen.Top,
            checked(virtualScreen.Left + virtualScreen.Width),
            checked(virtualScreen.Top + virtualScreen.Height));
    }

    internal static NativeMethods.Point ToCanvasCoordinates(
        NativeMethods.Rect virtualBounds,
        NativeMethods.Point screenPoint) =>
        new(
            checked(screenPoint.X - virtualBounds.Left),
            checked(screenPoint.Y - virtualBounds.Top));

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PortalShaderParameters(
        Vector4 SourceData,
        Vector4 OutputData,
        Vector4 ShapeData,
        Vector4 PortalData,
        Vector4 SourceData1,
        Vector4 SourceData2,
        Vector4 SourceData3);

    private sealed class CaptureSource : IDisposable
    {
        private CaptureSource(
            nint window,
            GraphicsCaptureItem captureItem,
            Direct3D11CaptureFramePool framePool,
            GraphicsCaptureSession session)
        {
            Window = window;
            CaptureItem = captureItem;
            FramePool = framePool;
            Session = session;
        }

        internal nint Window { get; }

        internal GraphicsCaptureItem CaptureItem { get; }

        internal Direct3D11CaptureFramePool FramePool { get; }

        internal GraphicsCaptureSession Session { get; }

        internal ID3D11Texture2D? Texture { get; set; }

        internal ID3D11ShaderResourceView? TextureView { get; set; }

        internal long FrameSerial { get; set; }

        internal static CaptureSource Create(
            nint window,
            IDirect3DDevice captureDevice)
        {
            var captureItem = GraphicsCaptureInterop.CreateForWindow(window);
            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;
            try
            {
                framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    captureDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    captureItem.Size);
                session = framePool.CreateCaptureSession(captureItem);
                session.IsCursorCaptureEnabled = false;
                return new CaptureSource(
                    window,
                    captureItem,
                    framePool,
                    session);
            }
            catch
            {
                session?.Dispose();
                framePool?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            TextureView?.Dispose();
            TextureView = null;
            Texture?.Dispose();
            Texture = null;
            Session.Dispose();
            FramePool.Dispose();
        }
    }

    private bool TryReconcileSources(
        NativeMethods.Point screenCenter,
        out string? error)
    {
        var now = Stopwatch.GetTimestamp();
        if (now < Volatile.Read(ref nextSourceReconciliationTimestamp))
        {
            error = null;
            return true;
        }

        Volatile.Write(
            ref nextSourceReconciliationTimestamp,
            now + (long)(Stopwatch.Frequency *
                         (SourceReconciliationMilliseconds / 1000d)));
        var sessionProtectedWindow = protectedWindow;
        if (sessionProtectedWindow == nint.Zero ||
            !NativeMethods.IsWindow(sessionProtectedWindow))
        {
            error = "F8 会话的宿主窗口已经不可用。";
            return false;
        }

        CaptureSource[] currentSources;
        lock (gpuLock)
        {
            if (!active)
            {
                error = "GPU 透视视觉源尚未启动。";
                return false;
            }

            currentSources = captureSources.ToArray();
        }

        var invalidCurrentSources = currentSources
            .Where(source =>
                !MultilayerWindowResolver.IsEligibleSessionSource(
                    source.Window))
            .Select(source => source.Window)
            .ToHashSet();
        if (invalidCurrentSources.Count == 0)
        {
            // Physical promotion deliberately changes the captured Z-order.
            // Do not interpret that valid reordering (or normal app-owned
            // reorder events) as a new source set. Reconcile only when an
            // existing capture actually closes, minimizes, or becomes invalid.
            error = null;
            return true;
        }

        var validCurrentHandles = currentSources
            .Select(source => source.Window)
            .Where(window => !invalidCurrentSources.Contains(window))
            .ToHashSet();
        var deepestValidWindow = currentSources
            .Select(source => source.Window)
            .LastOrDefault(validCurrentHandles.Contains);
        var resolvedSources = deepestValidWindow == nint.Zero
            ? MultilayerWindowResolver.Resolve(
                sessionProtectedWindow,
                geometry.CreateFrameBounds(screenCenter),
                validCurrentHandles)
            : MultilayerWindowResolver.ResolveAfterWindow(
                deepestValidWindow,
                geometry.CreateFrameBounds(screenCenter),
                validCurrentHandles);
        if (resolvedSources.Count <
            MultilayerWindowResolver.MaximumLayerCount -
            validCurrentHandles.Count)
        {
            var excludedWithDeeperCandidates = validCurrentHandles
                .Concat(resolvedSources.Select(source => source.Handle))
                .ToHashSet();
            resolvedSources = resolvedSources
                .Concat(MultilayerWindowResolver.Resolve(
                    sessionProtectedWindow,
                    geometry.CreateFrameBounds(screenCenter),
                    excludedWithDeeperCandidates))
                .Take(MultilayerWindowResolver.MaximumLayerCount)
                .ToArray();
        }
        var currentHandles = currentSources
            .Select(source => source.Window)
            .ToArray();
        var resolvedHandles = MultilayerWindowResolver.ReconcileInvalidSources(
                currentHandles,
                invalidCurrentSources,
                resolvedSources.Select(source => source.Handle))
            .ToArray();
        if (resolvedHandles.Length == 0)
        {
            // There is no safe replacement yet. The last complete composition
            // remains visible and a later pass can recover if a window appears.
            error = null;
            return true;
        }

        if (currentSources.Select(source => source.Window)
            .SequenceEqual(resolvedHandles))
        {
            error = null;
            return true;
        }

        var existingByWindow = currentSources.ToDictionary(
            source => source.Window);
        var addedSources = new List<CaptureSource>();
        var sourcesCommitted = false;
        var guardUpdated = false;
        try
        {
            foreach (var resolvedHandle in resolvedHandles)
            {
                if (existingByWindow.ContainsKey(resolvedHandle))
                {
                    continue;
                }

                var captureSource = CaptureSource.Create(
                    resolvedHandle,
                    resources.CaptureDevice);
                captureSource.FramePool.FrameArrived += OnFrameArrived;
                addedSources.Add(captureSource);
            }

            lock (gpuLock)
            {
                foreach (var addedSource in addedSources)
                {
                    captureSourceByPool.Add(
                        addedSource.FramePool,
                        addedSource);
                }
            }

            foreach (var addedSource in addedSources)
            {
                addedSource.Session.StartCapture();
            }

            if (!foregroundGuard.TryUpdateSources(resolvedHandles, out error))
            {
                RemovePendingSourcesNoThrow(addedSources);
                Interlocked.Increment(ref sourceReconciliationRetryCount);
                // The current capture list and last complete composition are
                // still usable. Retry the replacement later without exposing
                // the protected foreground window for a transient guard race.
                error = null;
                return true;
            }
            guardUpdated = true;

            CaptureSource[] removedSources;
            lock (gpuLock)
            {
                if (!active)
                {
                    RemovePendingSourcesLocked(addedSources);
                    error = "GPU 透视会话在更新来源时已经结束。";
                    return false;
                }

                var addedByWindow = addedSources.ToDictionary(
                    source => source.Window);
                var nextSources = resolvedHandles
                    .Select(window => existingByWindow.TryGetValue(
                            window,
                            out var existingSource)
                        ? existingSource
                        : addedByWindow[window])
                    .ToArray();
                removedSources = currentSources
                    .Where(source => !resolvedHandles.Contains(source.Window))
                    .ToArray();
                foreach (var removedSource in removedSources)
                {
                    captureSourceByPool.Remove(removedSource.FramePool);
                }

                captureSources.Clear();
                captureSources.AddRange(nextSources);
                sourcesCommitted = true;
                SourceWindow = resolvedHandles[0];
                compositionOrderSerial++;
                pendingOrderCaptureSerial = 0;
                pendingOrderSourceWindow = nint.Zero;
                pendingOrderSourceFrameSerial = 0;
                pendingOrderDeadlineTimestamp = 0;
                Interlocked.Increment(ref sourceReconciliationCount);
                Interlocked.Add(
                    ref sourceReplacementCount,
                    addedSources.Count);
            }

            foreach (var removedSource in removedSources)
            {
                removedSource.FramePool.FrameArrived -= OnFrameArrived;
                try
                {
                    removedSource.Dispose();
                }
                catch
                {
                    // The source has already been detached from the active
                    // frame-pool map. Disposal races cannot invalidate the
                    // committed replacement or its last presented frame.
                }
            }

            if (removedSources.Length > 0)
            {
                // Closing a non-activating source can make Windows choose an
                // unrelated fallback foreground window. Re-assert the F8 host
                // after the source set and its physical -1 are synchronized.
                foregroundGuard.RestoreProtectedForegroundAfterSourceRemoval();
            }

            error = null;
            return true;
        }
        catch (Exception exception)
        {
            if (!sourcesCommitted)
            {
                RemovePendingSourcesNoThrow(addedSources);
                if (guardUpdated)
                {
                    _ = foregroundGuard.TryUpdateSources(
                        currentHandles,
                        out _);
                }
            }
            _ = exception;
            Interlocked.Increment(ref sourceReconciliationRetryCount);
            // Dynamic backfill is an optional in-session repair. Its failure
            // is recoverable while an already-presented composition exists.
            // Keep that frame visible and retry on the next scheduled pass.
            error = null;
            return true;
        }
    }

    private void RemovePendingSourcesNoThrow(
        IReadOnlyList<CaptureSource> sources)
    {
        lock (gpuLock)
        {
            RemovePendingSourcesLocked(sources);
        }

        foreach (var source in sources)
        {
            try
            {
                source.FramePool.FrameArrived -= OnFrameArrived;
                source.Dispose();
            }
            catch
            {
                // Best-effort cleanup for capture sessions that never became
                // part of the committed source set.
            }
        }
    }

    private void RemovePendingSourcesLocked(
        IReadOnlyList<CaptureSource> sources)
    {
        foreach (var source in sources)
        {
            captureSourceByPool.Remove(source.FramePool);
        }
    }

    internal bool TryPromoteSource(nint sourceWindow, out string? error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gpuLock)
        {
            if (!active || captureSources.All(source => source.Window != sourceWindow))
            {
                error = "鼠标命中的窗口不属于当前多层 GPU 会话。";
                return false;
            }
        }

        if (!foregroundGuard.TryPromoteSource(sourceWindow, out error))
        {
            return false;
        }

        lock (gpuLock)
        {
            var sourceIndex = captureSources.FindIndex(
                source => source.Window == sourceWindow);
            if (sourceIndex > 0)
            {
                pendingOrderCaptureSerial = Interlocked.Read(
                    ref latestCaptureSerial);
                var promotedSource = captureSources[sourceIndex];
                pendingOrderSourceWindow = promotedSource.Window;
                pendingOrderSourceFrameSerial = promotedSource.FrameSerial;
                pendingOrderDeadlineTimestamp = Stopwatch.GetTimestamp() +
                    (long)(Stopwatch.Frequency *
                           (OrderSwitchMaximumHoldMilliseconds / 1000d));
                var reordered = MultilayerWindowResolver.PromoteToFront(
                    captureSources,
                    promotedSource);
                captureSources.Clear();
                captureSources.AddRange(reordered);
                SourceWindow = sourceWindow;
                compositionOrderSerial++;
            }
        }

        error = null;
        return true;
    }

    internal bool ContainsSourceWindow(nint sourceWindow)
    {
        lock (gpuLock)
        {
            return active && captureSources.Any(
                source => source.Window == sourceWindow);
        }
    }

    private sealed class PortalGpuForm : Form
    {
        private const int WmNcHitTest = 0x0084;
        private const int WmMouseActivate = 0x0021;
        private const int HtTransparent = -1;
        private const int MaNoActivate = 3;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
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

        internal bool HasInputPassThrough
        {
            get
            {
                var extendedStyle = NativeMethods.GetWindowLongPtr(
                    Handle,
                    NativeMethods.GwlExStyle).ToInt64();
                var requiredStyles = WsExLayered | WsExTransparent;
                return (extendedStyle & requiredStyles) == requiredStyles &&
                    NativeMethods.SendMessage(
                        Handle,
                        WmNcHitTest,
                        nint.Zero,
                        nint.Zero) == new nint(HtTransparent) &&
                    NativeMethods.SendMessage(
                        Handle,
                        WmMouseActivate,
                        nint.Zero,
                        nint.Zero) == new nint(MaNoActivate);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |=
                    WsExTransparent |
                    WsExToolWindow |
                    WsExLayered |
                    WsExNoActivate |
                    WsExNoRedirectionBitmap;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                message.Result = new nint(HtTransparent);
                return;
            }

            if (message.Msg == WmMouseActivate)
            {
                message.Result = new nint(MaNoActivate);
                return;
            }

            base.WndProc(ref message);
        }
    }
}
