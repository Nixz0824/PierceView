using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace WindowPortal;

internal static class GraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemInterfaceId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropId =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private const string GraphicsCaptureItemRuntimeClass =
        "Windows.Graphics.Capture.GraphicsCaptureItem";

    internal static GraphicsCaptureItem CreateForWindow(nint window)
    {
        if (window == nint.Zero || !NativeMethods.IsWindow(window))
        {
            throw new ArgumentException("指定的 HWND 不是有效窗口。", nameof(window));
        }

        var createStringResult = WindowsCreateString(
            GraphicsCaptureItemRuntimeClass,
            GraphicsCaptureItemRuntimeClass.Length,
            out var runtimeClassName);
        Marshal.ThrowExceptionForHR(createStringResult);
        try
        {
            var factoryInterfaceId = GraphicsCaptureItemInteropId;
            var factoryResult = RoGetActivationFactory(
                runtimeClassName,
                ref factoryInterfaceId,
                out var factoryPointer);
            Marshal.ThrowExceptionForHR(factoryResult);
            object? activationFactory = null;
            try
            {
                activationFactory = Marshal.GetObjectForIUnknown(factoryPointer);
                var interop = (IGraphicsCaptureItemInterop)activationFactory;
                var itemInterfaceId = GraphicsCaptureItemInterfaceId;
                var result = interop.CreateForWindow(
                    window,
                    ref itemInterfaceId,
                    out var itemPointer);
                Marshal.ThrowExceptionForHR(result);
                if (itemPointer == nint.Zero)
                {
                    throw new InvalidOperationException(
                        "WGC 未返回 GraphicsCaptureItem。");
                }

                try
                {
                    return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(
                        itemPointer);
                }
                finally
                {
                    _ = Marshal.Release(itemPointer);
                }
            }
            finally
            {
                if (activationFactory is not null &&
                    Marshal.IsComObject(activationFactory))
                {
                    _ = Marshal.FinalReleaseComObject(activationFactory);
                }

                _ = Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            _ = WindowsDeleteString(runtimeClassName);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid interfaceId,
        out nint factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(
            nint window,
            ref Guid interfaceId,
            out nint result);

        [PreserveSig]
        int CreateForMonitor(
            nint monitor,
            ref Guid interfaceId,
            out nint result);
    }
}
