using System.Reflection;
using System.Runtime.InteropServices;

namespace WindowPortal;

internal static class BrandResources
{
    private const string LogoResourceName = "PierceView.Logo.png";

    internal static Bitmap? LoadLogoBitmap()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(LogoResourceName);
        if (stream is null)
        {
            return null;
        }

        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    }

    internal static Icon LoadApplicationIcon()
    {
        using var source = LoadLogoBitmap();
        if (source is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using var square = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(square))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            var scale = Math.Min(32f / source.Width, 32f / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            graphics.DrawImage(source, (32 - width) / 2, (32 - height) / 2, width, height);
        }

        var handle = square.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
