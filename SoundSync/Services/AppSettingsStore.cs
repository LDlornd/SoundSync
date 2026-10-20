using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundSync.Models;

namespace SoundSync.Services;

internal sealed class AppSettingsStore(string path)
{
    // Unset window coordinates use NaN. Without this option, saving a preference before
    // the first window close throws and silently loses the user's new switch settings.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal AppSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new()
                : new();
        }
        catch { return new(); }
    }

    internal void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}
