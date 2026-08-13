namespace WindowPortal;

/// <summary>
/// Prefers the WGC/D3D11 renderer and keeps the proven DWM/CPU renderer as a
/// per-session fallback. A GPU failure never removes the existing portal path.
/// </summary>
internal sealed class AdaptivePortalOverlay : IDisposable
{
    private readonly DwmPortalOverlay cpuOverlay;
    private readonly GpuPortalOverlay? gpuOverlay;
    private ActiveBackend activeBackend;
    private nint activeSourceWindow;
    private nint protectedWindow;

    internal AdaptivePortalOverlay(PortalGeometry geometry)
    {
        cpuOverlay = new DwmPortalOverlay(
            geometry,
            enableForegroundGuard: true,
            lateLatchToCursor: true);
        try
        {
            gpuOverlay = new GpuPortalOverlay(geometry);
        }
        catch
        {
            gpuOverlay = null;
        }
    }

    internal bool IsVisible => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.IsVisible == true,
        ActiveBackend.Cpu => cpuOverlay.IsVisible,
        _ => false,
    };

    internal bool IsGpuActive =>
        activeBackend == ActiveBackend.Gpu && gpuOverlay?.IsVisible == true;

    internal string ActiveBackendName => activeBackend switch
    {
        ActiveBackend.Gpu => "GPU/WGC",
        ActiveBackend.Cpu => "CPU/DWM",
        _ => "None",
    };

    internal int ForegroundRecoveryCount => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.ForegroundRecoveryCount ?? 0,
        ActiveBackend.Cpu => cpuOverlay.ForegroundRecoveryCount,
        _ => 0,
    };

    internal int ImmediateForegroundClampCount => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.ImmediateForegroundClampCount ?? 0,
        ActiveBackend.Cpu => cpuOverlay.ImmediateForegroundClampCount,
        _ => 0,
    };

    internal int VisualPlacementCount => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.DisplayPlacementCount ?? 0,
        ActiveBackend.Cpu => cpuOverlay.DisplayRelocationCount,
        _ => 0,
    };

    internal int BackgroundPromotionCount => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.BackgroundPromotionCount ?? 0,
        ActiveBackend.Cpu => cpuOverlay.BackgroundPromotionCount,
        _ => 0,
    };

    internal bool HasPresentedFrame => activeBackend switch
    {
        ActiveBackend.Gpu => (gpuOverlay?.PresentedFrames ?? 0) > 0,
        ActiveBackend.Cpu => cpuOverlay.IsVisible,
        _ => false,
    };

    internal bool IsSourceNoActivateApplied
    {
        get
        {
            var sourceWindow = SourceWindow;
            if (sourceWindow == nint.Zero || !NativeMethods.IsWindow(sourceWindow))
            {
                return false;
            }

            var extendedStyle = NativeMethods.GetWindowLongPtr(
                sourceWindow,
                NativeMethods.GwlExStyle);
            return (extendedStyle.ToInt64() & NativeMethods.WsExNoActivate) != 0;
        }
    }

    internal nint SourceWindow => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.SourceWindow ?? nint.Zero,
        ActiveBackend.Cpu => cpuOverlay.SourceWindow,
        _ => nint.Zero,
    };

    internal int SourceCount => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.SourceCount ?? 0,
        ActiveBackend.Cpu => cpuOverlay.IsVisible ? 1 : 0,
        _ => 0,
    };

    internal NativeMethods.Point? LastPresentedCenter => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.LastPresentedCenter,
        ActiveBackend.Cpu => cpuOverlay.LastPresentedCenter,
        _ => null,
    };

    internal bool TryShow(
        nint sourceWindow,
        nint protectedWindow,
        NativeMethods.Point screenCenter,
        out string? error)
    {
        return TryShow(
            [new MultilayerWindowSource(sourceWindow, default)],
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
        Hide();
        if (sources.Count == 0)
        {
            error = "没有识别到宿主窗口后方与矩形透视区域相交的窗口。";
            return false;
        }

        activeSourceWindow = sources[0].Handle;
        this.protectedWindow = protectedWindow;
        string? gpuError = null;
        if (gpuOverlay is not null &&
            gpuOverlay.TryShow(
                sources,
                protectedWindow,
                screenCenter,
                out gpuError))
        {
            activeBackend = ActiveBackend.Gpu;
            error = null;
            return true;
        }

        string? cpuError = null;
        if (cpuOverlay.TryShow(
                activeSourceWindow,
                protectedWindow,
                screenCenter,
                out cpuError))
        {
            activeBackend = ActiveBackend.Cpu;
            error = null;
            return true;
        }

        error = gpuError is null
            ? cpuError
            : $"GPU 路径：{gpuError} CPU 回退：{cpuError}";
        activeSourceWindow = nint.Zero;
        this.protectedWindow = nint.Zero;
        return false;
    }

    internal bool TryUpdate(
        NativeMethods.Point screenCenter,
        out string? error)
    {
        switch (activeBackend)
        {
            case ActiveBackend.Gpu when gpuOverlay is not null:
                if (gpuOverlay.TryUpdate(screenCenter, out error))
                {
                    return true;
                }

                var gpuError = error;
                gpuOverlay.Hide();
                activeBackend = ActiveBackend.None;
                string? cpuError = null;
                if (activeSourceWindow != nint.Zero &&
                    protectedWindow != nint.Zero &&
                    cpuOverlay.TryShow(
                        activeSourceWindow,
                        protectedWindow,
                        screenCenter,
                        out cpuError))
                {
                    activeBackend = ActiveBackend.Cpu;
                    error = null;
                    return true;
                }

                error = $"GPU 路径：{gpuError} CPU 回退：{cpuError}";
                return false;
            case ActiveBackend.Cpu:
                return cpuOverlay.TryUpdate(screenCenter, out error);
            default:
                error = "透视视觉源尚未启动。";
                return false;
        }
    }

    internal bool TryPromoteSource(nint sourceWindow, out string? error)
    {
        if (activeBackend == ActiveBackend.Gpu && gpuOverlay is not null)
        {
            if (!gpuOverlay.TryPromoteSource(sourceWindow, out error))
            {
                return false;
            }

            activeSourceWindow = sourceWindow;
            return true;
        }

        if (activeBackend == ActiveBackend.Cpu && sourceWindow == activeSourceWindow)
        {
            error = null;
            return true;
        }

        error = "深层窗口提升仅在多层 GPU 会话中可用。";
        return false;
    }

    internal bool ContainsSourceWindow(nint sourceWindow) =>
        activeBackend switch
        {
            ActiveBackend.Gpu =>
                gpuOverlay?.ContainsSourceWindow(sourceWindow) == true,
            ActiveBackend.Cpu => sourceWindow == activeSourceWindow,
            _ => false,
        };

    internal void Hide()
    {
        gpuOverlay?.Hide();
        cpuOverlay.Hide();
        activeBackend = ActiveBackend.None;
        activeSourceWindow = nint.Zero;
        protectedWindow = nint.Zero;
    }

    public void Dispose()
    {
        Hide();
        gpuOverlay?.Dispose();
        cpuOverlay.Dispose();
    }

    private enum ActiveBackend
    {
        None,
        Gpu,
        Cpu,
    }
}
