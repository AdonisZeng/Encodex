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

    private const string BackupExtension = ".bak";

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

    /// <summary>Persists the settings atomically: the JSON goes to a scratch file
    /// first, so a crash mid-write leaves the previous settings intact instead of
    /// a truncated file, and only then is swapped into place.</summary>
    /// <remarks>All failures are swallowed on purpose — settings are a convenience,
    /// and an unwritable %APPDATA% must never break the app.</remarks>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? ".");
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_settingsPath))
                ReplaceFromTemp(tempPath);
            else
                File.Move(tempPath, _settingsPath);
        }
        catch
        {
            // Failing to persist settings must never break the app.
        }
    }

    /// <summary>Swaps the scratch file into place while keeping a backup copy.
    /// The backup name is mandatory: <see cref="File.Replace"/> with a null backup
    /// name can fail with ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176), which removes
    /// the destination and leaves nothing to restore — the user's settings would
    /// silently vanish (same pitfall as EncodingConverter.TryReplaceFile).</summary>
    private void ReplaceFromTemp(string tempPath)
    {
        var backupPath = _settingsPath + BackupExtension;

        // ReplaceFile rejects an already existing backup name, so a leftover from
        // a crashed save must be removed first. Deleting it is safe: the backup is
        // always a copy of settings.json, never the only good copy.
        TryDeleteQuietly(backupPath);
        try
        {
            File.Replace(tempPath, _settingsPath, backupPath);

            // The swap already succeeded, so a lingering backup is mere garbage and
            // must not turn a successful save into a failed one.
            TryDeleteQuietly(backupPath);
        }
        catch
        {
            // Recovery belongs here because only this frame knows the backup path;
            // it is best effort and must not mask the replace failure. When the
            // destination survived, the backup is an ordinary leftover and must be
            // dropped — otherwise a save that keeps failing would litter %APPDATA%.
            if (File.Exists(_settingsPath))
                TryDeleteQuietly(backupPath);
            else
                TryRestoreBackup(backupPath);
        }
    }

    /// <summary>Restores settings.json after a failed replace. Error 1176 can leave
    /// the destination missing with the backup holding the only remaining copy, so
    /// the backup is moved back; if the destination survived, it is left alone.</summary>
    private void TryRestoreBackup(string backupPath)
    {
        try
        {
            if (!File.Exists(_settingsPath) && File.Exists(backupPath))
                File.Move(backupPath, _settingsPath);
        }
        catch
        {
            // Recovery is best effort and must never mask the original failure.
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { }
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

    /// <summary>Custom (non-default) extensions added by the user, persisted so they
    /// survive a restart.</summary>
    public List<string> CustomExtensions { get; set; } = new();
}
