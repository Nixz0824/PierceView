using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace WindowPortal;

internal sealed class GpuDeviceResources : IDisposable
{
    private bool disposed;

    private GpuDeviceResources(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIDevice dxgiDevice,
        IDirect3DDevice captureDevice,
        IDCompositionDevice compositionDevice,
        AdapterDescription adapterDescription)
    {
        Device = device;
        Context = context;
        DxgiDevice = dxgiDevice;
        CaptureDevice = captureDevice;
        CompositionDevice = compositionDevice;
        AdapterDescription = adapterDescription;
    }

    internal ID3D11Device Device { get; }

    internal ID3D11DeviceContext Context { get; }

    internal IDXGIDevice DxgiDevice { get; }

    internal IDirect3DDevice CaptureDevice { get; }

    internal IDCompositionDevice CompositionDevice { get; }

    internal AdapterDescription AdapterDescription { get; }

    internal static GpuDeviceResources Create()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_12_1,
            FeatureLevel.Level_12_0,
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        };
        var creationFlags =
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        var result = D3D11CreateDevice(
            nint.Zero,
            DriverType.Hardware,
            creationFlags,
            featureLevels,
            out var device,
            out var featureLevel,
            out var context);
        result.CheckError();

        IDXGIDevice? dxgiDevice = null;
        IDirect3DDevice? captureDevice = null;
        IDCompositionDevice? compositionDevice = null;
        try
        {
            dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var captureDeviceResult = CreateWinRtDirect3D11Device(
                dxgiDevice.NativePointer,
                out var captureDevicePointer);
            Marshal.ThrowExceptionForHR(captureDeviceResult);
            try
            {
                captureDevice =
                    WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(
                        captureDevicePointer);
            }
            finally
            {
                _ = Marshal.Release(captureDevicePointer);
            }
            compositionDevice =
                DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

            if (device.FeatureLevel != featureLevel)
            {
                throw new InvalidOperationException(
                    $"D3D11 FeatureLevel 不一致：{device.FeatureLevel} / {featureLevel}。");
            }

            return new GpuDeviceResources(
                device,
                context,
                dxgiDevice,
                captureDevice,
                compositionDevice,
                adapter.Description);
        }
        catch
        {
            compositionDevice?.Dispose();
            ReleaseWinRt(captureDevice);
            dxgiDevice?.Dispose();
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CompositionDevice.Dispose();
        ReleaseWinRt(CaptureDevice);
        DxgiDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    private static void ReleaseWinRt(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateWinRtDirect3D11Device(
        nint dxgiDevice,
        out nint graphicsDevice);
}
