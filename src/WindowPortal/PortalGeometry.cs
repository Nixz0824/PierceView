namespace WindowPortal;

internal enum PortalShape
{
    Circle,
    Rectangle
}

internal readonly record struct PortalGeometry(
    PortalShape Shape,
    int Radius,
    int Width,
    int Height,
    int FeatherWidth)
{
    internal int FrameWidth => Shape == PortalShape.Circle
        ? checked((Radius * 2) + 1)
        : Width;

    internal int FrameHeight => Shape == PortalShape.Circle
        ? checked((Radius * 2) + 1)
        : Height;

    internal int GuardRadius => Math.Max(FrameWidth, FrameHeight) / 2;

    internal int EffectiveFeatherWidth => Shape == PortalShape.Rectangle
        ? Math.Clamp(
            FeatherWidth,
            0,
            Math.Min((FrameWidth - 1) / 2, (FrameHeight - 1) / 2))
        : 0;

    internal static PortalGeometry Circle(int radius) =>
        new(PortalShape.Circle, radius, 0, 0, 0);

    internal static PortalGeometry Rectangle(int width, int height, int featherWidth = 0) =>
        new(PortalShape.Rectangle, 0, width, height, featherWidth);

    internal NativeMethods.Rect CreateFrameBounds(NativeMethods.Point center)
    {
        var left = center.X - (FrameWidth / 2);
        var top = center.Y - (FrameHeight / 2);
        return new NativeMethods.Rect(left, top, left + FrameWidth, top + FrameHeight);
    }

    internal NativeMethods.Rect CreateHitBounds(NativeMethods.Point center)
    {
        var frame = CreateFrameBounds(center);
        var inset = EffectiveFeatherWidth;
        if (inset <= 0)
        {
            return frame;
        }

        return new NativeMethods.Rect(
            frame.Left + inset,
            frame.Top + inset,
            frame.Right - inset,
            frame.Bottom - inset);
    }
}
