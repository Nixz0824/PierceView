using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace WindowPortal;

internal static class GpuCapabilityProbe
{
    private const int EnumCurrentSettings = -1;

    internal static int Run()
    {
        Console.WriteLine("寸镜 / PierceView GPU 能力探针");
        Console.WriteLine($"Windows={Environment.OSVersion.Version}，64 位进程={Environment.Is64BitProcess}。");

        _ = Application.OleRequired();

        var screen = Screen.FromPoint(Cursor.Position);
        var displayMode = ReadCurrentDisplayMode(screen.DeviceName);
        if (displayMode is { } mode)
        {
            var refreshPeriod = 1000.0 / mode.RefreshRate;
            Console.WriteLine(
                $"显示器={screen.DeviceName}，{mode.Width}×{mode.Height} @ {mode.RefreshRate}Hz，" +
                $"每帧预算={refreshPeriod:F2}ms。");
        }
        else
        {
            Console.WriteLine($"显示器={screen.DeviceName}，Windows 未返回当前刷新率。");
        }

        bool captureSupported;
        try
        {
            captureSupported = GraphicsCaptureSession.IsSupported();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"WGC 能力查询失败：HRESULT=0x{exception.HResult:X8}，" +
                $"{exception.GetType().Name}：{exception.Message}");
            return 10;
        }

        Console.WriteLine($"Windows.Graphics.Capture={FormatSupport(captureSupported)}。");
        if (!captureSupported)
        {
            Console.Error.WriteLine("当前系统不支持 WGC，GPU 渲染器将不可用。");
            return 10;
        }

        try
        {
            using var resources = GpuDeviceResources.Create();
            var adapterDescription = resources.AdapterDescription;

            Console.WriteLine(
                $"D3D11=硬件，FeatureLevel={resources.Device.FeatureLevel}，" +
                $"BGRA=True，Video=True。");
            Console.WriteLine(
                $"DXGI 显卡={adapterDescription.Description.TrimEnd('\0')}，" +
                $"专用显存={FormatBytes((ulong)adapterDescription.DedicatedVideoMemory)}。");

            Console.WriteLine("D3D11 → WinRT IDirect3DDevice=成功。");
            var stateOk = resources.CompositionDevice.CheckDeviceState();
            Console.WriteLine($"DirectComposition=成功，设备状态={FormatSupport(stateOk)}。");

            using var rendererProbe = new GpuPortalOverlay(
                PortalGeometry.Rectangle(420, 280, 24));
            Console.WriteLine("GPU 形状/羽化 HLSL 与合成交换链=成功。");

            Console.WriteLine(
                "GPU 前置条件通过：可继续建立 HWND WGC → D3D11 纹理 → " +
                "DirectComposition 的最小闭环。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"GPU 前置条件失败：{exception.GetType().Name}：{exception.Message}");
            return 11;
        }
    }

    private static DisplayMode? ReadCurrentDisplayMode(string deviceName)
    {
        var mode = DevMode.Create();
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode) ||
            mode.DisplayFrequency == 0)
        {
            return null;
        }

        return new DisplayMode(
            checked((int)mode.PelsWidth),
            checked((int)mode.PelsHeight),
            checked((int)mode.DisplayFrequency));
    }

    private static string FormatSupport(bool supported) => supported ? "支持" : "不支持";

    private static string FormatBytes(ulong bytes)
    {
        const double gibibyte = 1024d * 1024d * 1024d;
        return $"{bytes / gibibyte:F1} GiB";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string deviceName,
        int modeNumber,
        ref DevMode mode);

    private readonly record struct DisplayMode(int Width, int Height, int RefreshRate);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;

        public static DevMode Create()
        {
            var mode = new DevMode
            {
                DeviceName = string.Empty,
                FormName = string.Empty,
            };
            mode.Size = checked((ushort)Marshal.SizeOf<DevMode>());
            return mode;
        }
    }
}
