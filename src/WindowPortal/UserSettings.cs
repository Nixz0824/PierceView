using System.Text.Json;

namespace WindowPortal;

internal sealed record UserSettings(
    int Radius,
    string Language)
{
    internal const int DefaultRadius = 180;
    internal const int MinimumRadius = 64;
    internal const int MaximumRadius = 400;

    internal static UserSettings CreateDefault() =>
        new(
            DefaultRadius,
            Localizer.DefaultLanguage);

    internal UserSettings Normalize() =>
        this with
        {
            Radius = Math.Clamp(Radius, MinimumRadius, MaximumRadius),
            Language = Localizer.NormalizeLanguage(Language)
        };
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
