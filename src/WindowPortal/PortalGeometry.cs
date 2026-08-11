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
    private const int MaximumInteractionRadius = 32;

    internal int FrameWidth => Shape == PortalShape.Circle
        ? checked((Radius * 2) + 1)
        : Width;

    internal int FrameHeight => Shape == PortalShape.Circle
        ? checked((Radius * 2) + 1)
        : Height;

    internal int GuardRadius => Math.Max(FrameWidth, FrameHeight) / 2;

    internal int EffectiveFeatherWidth => Math.Clamp(
        FeatherWidth,
        0,
        Shape == PortalShape.Circle
            ? Math.Max(0, Radius - 1)
            : Math.Min((FrameWidth - 1) / 2, (FrameHeight - 1) / 2));

    internal int EffectiveHitRadius => Shape == PortalShape.Circle
        ? Math.Max(1, Radius - EffectiveFeatherWidth)
        : 0;

    /// <summary>
    /// The visual portal is large, but mouse input only needs a small aperture around
    /// the cursor. Keeping the physical window-region hole small prevents a stale
    /// full-size circle/rectangle from flashing while the layered visual moves.
    /// </summary>
    internal int EffectiveInteractionRadius
    {
        get
        {
            var clearHalfExtent = Shape == PortalShape.Circle
                ? EffectiveHitRadius
                : Math.Max(
                    1,
                    (Math.Min(FrameWidth, FrameHeight) -
                     (EffectiveFeatherWidth * 2)) / 2);
            return Math.Clamp(clearHalfExtent, 1, MaximumInteractionRadius);
        }
    }

    /// <summary>
    /// Keep the physical aperture anchored until the pointer has consumed half of
    /// its radius. Small wheel-time pointer jitter therefore remains inside the same
    /// native hit-test hole without moving the host Region every render tick.
    /// </summary>
    internal int InteractionReanchorDistance =>
        Math.Max(2, EffectiveInteractionRadius / 2);

    internal int EffectiveCornerRadius
    {
        get
        {
            if (Shape != PortalShape.Rectangle)
            {
                return 0;
            }

            var geometryLimit = Math.Min(
                (FrameWidth - 1) / 2,
                (FrameHeight - 1) / 2);
            return Math.Clamp(
                Math.Min(FrameWidth, FrameHeight) / 6,
                Math.Min(20, geometryLimit),
                geometryLimit);
        }
    }

    internal int EffectiveHitCornerRadius =>
        Math.Max(0, EffectiveCornerRadius - EffectiveFeatherWidth);

    internal static PortalGeometry Circle(int radius, int featherWidth = 0) =>
        new(PortalShape.Circle, radius, 0, 0, featherWidth);

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
