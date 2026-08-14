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

            // Atomic write: write a temp file first, then replace. A crash mid-write
            // leaves the previous settings intact instead of a truncated file.
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_settingsPath))
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _settingsPath);
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
    public string? SourceFolderPath { get; set; }

    /// <summary>DisplayName of the last selected target encoding.</summary>
    public string? SelectedEncoding { get; set; }

    /// <summary>Extensions that were checked when the settings were saved.</summary>
    public List<string> SelectedExtensions { get; set; } = new();
}
