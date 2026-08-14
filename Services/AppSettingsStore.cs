using System.IO;
using System.Text.Json;

namespace Encodex.Services;

/// <summary>Persists lightweight app settings (e.g. theme) to %APPDATA%\Encodex\settings.json.</summary>
public class AppSettingsStore
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Encodex",
        "settings.json");

    private readonly string _settingsPath;

    public AppSettingsStore()
        : this(DefaultPath)
    {
    }

    /// <param name="settingsPath">Custom settings file path (used by tests).</param>
    internal AppSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? ".");
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Failing to persist settings must never break the app.
        }
    }
}

public class AppSettings
{
    public bool IsLightTheme { get; set; }
}
