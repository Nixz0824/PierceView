using System.Text.Json;

namespace WindowPortal;

internal sealed record UserSettings(
    int Radius,
    string Language,
    string? PortalMode = null,
    int RectangleWidth = 420,
    int RectangleHeight = 280,
    int FeatherWidth = 24)
{
    internal const string CircleMode = "circle";
    internal const string RectangleMode = "rectangle";
    internal const int DefaultRadius = 180;
    internal const int MinimumRadius = 64;
    internal const int MaximumRadius = 400;
    internal const int DefaultRectangleWidth = 420;
    internal const int DefaultRectangleHeight = 280;
    internal const int MinimumRectangleWidth = 160;
    internal const int MaximumRectangleWidth = 1000;
    internal const int MinimumRectangleHeight = 120;
    internal const int MaximumRectangleHeight = 800;
    internal const int DefaultFeatherWidth = 24;
    internal const int MinimumFeatherWidth = 0;
    internal const int MaximumFeatherWidth = 80;

    internal static UserSettings CreateDefault() =>
        new(
            DefaultRadius,
            Localizer.DefaultLanguage,
            RectangleMode,
            DefaultRectangleWidth,
            DefaultRectangleHeight,
            DefaultFeatherWidth);

    internal UserSettings Normalize() =>
        this with
        {
            Radius = Math.Clamp(Radius, MinimumRadius, MaximumRadius),
            Language = Localizer.NormalizeLanguage(Language),
            PortalMode = NormalizePortalMode(PortalMode),
            RectangleWidth = NormalizeRectangleDimension(
                RectangleWidth,
                DefaultRectangleWidth,
                MinimumRectangleWidth,
                MaximumRectangleWidth),
            RectangleHeight = NormalizeRectangleDimension(
                RectangleHeight,
                DefaultRectangleHeight,
                MinimumRectangleHeight,
                MaximumRectangleHeight),
            FeatherWidth = NormalizeFeatherWidth(
                FeatherWidth,
                RectangleWidth,
                RectangleHeight)
        };

    internal PortalGeometry CreateGeometry() =>
        string.Equals(PortalMode, RectangleMode, StringComparison.OrdinalIgnoreCase)
            ? PortalGeometry.Rectangle(RectangleWidth, RectangleHeight, FeatherWidth)
            : PortalGeometry.Circle(
                checked(Radius + FeatherWidth),
                FeatherWidth);

    private static string NormalizePortalMode(string? mode) =>
        string.Equals(mode, RectangleMode, StringComparison.OrdinalIgnoreCase)
            ? RectangleMode
            : CircleMode;

    private static int NormalizeRectangleDimension(
        int value,
        int defaultValue,
        int minimum,
        int maximum) =>
        value == 0 ? defaultValue : Math.Clamp(value, minimum, maximum);

    private static int NormalizeFeatherWidth(int value, int width, int height)
    {
        var normalizedWidth = NormalizeRectangleDimension(
            width,
            DefaultRectangleWidth,
            MinimumRectangleWidth,
            MaximumRectangleWidth);
        var normalizedHeight = NormalizeRectangleDimension(
            height,
            DefaultRectangleHeight,
            MinimumRectangleHeight,
            MaximumRectangleHeight);
        var geometryLimit = Math.Min(
            (normalizedWidth - 1) / 2,
            (normalizedHeight - 1) / 2);
        return Math.Clamp(
            value,
            MinimumFeatherWidth,
            Math.Min(MaximumFeatherWidth, geometryLimit));
    }
}

internal sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    internal UserSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PierceView",
            "settings.json");
    }

    internal bool Exists => File.Exists(_path);

    internal string PathForDiagnostics => _path;

    internal UserSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return UserSettings.CreateDefault();
            }

            var json = File.ReadAllText(_path);
            return (JsonSerializer.Deserialize<UserSettings>(json) ??
                    UserSettings.CreateDefault())
                .Normalize();
        }
        catch
        {
            return UserSettings.CreateDefault();
        }
    }

    internal void Save(UserSettings settings)
    {
        var normalized = settings.Normalize();
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
