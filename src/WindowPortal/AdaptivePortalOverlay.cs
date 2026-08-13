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

    internal nint SourceWindow => activeBackend switch
    {
        ActiveBackend.Gpu => gpuOverlay?.SourceWindow ?? nint.Zero,
        ActiveBackend.Cpu => cpuOverlay.SourceWindow,
        _ => nint.Zero,
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
        Hide();
        activeSourceWindow = sourceWindow;
        this.protectedWindow = protectedWindow;
        string? gpuError = null;
        if (gpuOverlay is not null &&
            gpuOverlay.TryShow(
                sourceWindow,
                protectedWindow,
                screenCenter,
                out gpuError))
        {
            activeBackend = ActiveBackend.Gpu;
            error = null;
            return true;
        }

        if (cpuOverlay.TryShow(
                sourceWindow,
                protectedWindow,
                screenCenter,
                out var cpuError))
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
