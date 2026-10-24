using System.IO;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public AppSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EncodexSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string SettingsPath(string name = "settings.json") => Path.Combine(_tempDir, name);

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new AppSettingsStore(SettingsPath());

        var settings = store.Load();

        Assert.False(settings.IsLightTheme);
        Assert.Null(settings.SourceFolderPath);
        Assert.Null(settings.SelectedEncoding);
        Assert.Empty(settings.SelectedExtensions);
        Assert.Empty(settings.CustomExtensions);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        var path = SettingsPath();
        File.WriteAllText(path, "{ this is not valid json !!!");

        var settings = new AppSettingsStore(path).Load();

        Assert.False(settings.IsLightTheme);
        Assert.Empty(settings.SelectedExtensions);
    }

    [Fact]
    public void Load_UnknownJsonFields_AreIgnored()
    {
        var path = SettingsPath();
        File.WriteAllText(path, """{"IsLightTheme":true,"FutureField":42}""");

        var settings = new AppSettingsStore(path).Load();

        Assert.True(settings.IsLightTheme);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var path = SettingsPath();
        var store = new AppSettingsStore(path);
        var original = new AppSettings
        {
            IsLightTheme = true,
            SourceFolderPath = @"D:\src\project",
            SelectedEncoding = "GBK",
            SelectedExtensions = new List<string> { ".cs", ".txt" },
            CustomExtensions = new List<string> { ".log" }
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.True(loaded.IsLightTheme);
        Assert.Equal(@"D:\src\project", loaded.SourceFolderPath);
        Assert.Equal("GBK", loaded.SelectedEncoding);
        Assert.Equal(new[] { ".cs", ".txt" }, loaded.SelectedExtensions);
        Assert.Equal(new[] { ".log" }, loaded.CustomExtensions);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var path = Path.Combine(_tempDir, "nested", "deeper", "settings.json");

        new AppSettingsStore(path).Save(new AppSettings { IsLightTheme = true });

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_OverExistingFile_ReplacesIt()
    {
        // The second save exercises the File.Replace branch of the atomic write.
        var path = SettingsPath();
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings { IsLightTheme = false, SourceFolderPath = "first" });

        store.Save(new AppSettings { IsLightTheme = true, SourceFolderPath = "second" });

        var loaded = store.Load();
        Assert.True(loaded.IsLightTheme);
        Assert.Equal("second", loaded.SourceFolderPath);
        // The atomic write must not leave its scratch file behind.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_DoesNotLeaveBackupFile()
    {
        // File.Replace needs a backup name to stay recoverable, but that backup is
        // only a transient artefact and must never outlive a successful save.
        var path = SettingsPath();
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings { SourceFolderPath = "first" });

        store.Save(new AppSettings { SourceFolderPath = "second" });

        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Save_WithStaleBackupFile_StillReplacesAndCleansUp()
    {
        // A backup left behind by a crashed save must not block the next write:
        // ReplaceFile rejects an already existing backup name.
        var path = SettingsPath();
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings { SourceFolderPath = "first" });
        File.WriteAllText(path + ".bak", "stale backup");

        store.Save(new AppSettings { IsLightTheme = true, SourceFolderPath = "second" });

        var loaded = store.Load();
        Assert.True(loaded.IsLightTheme);
        Assert.Equal("second", loaded.SourceFolderPath);
        Assert.False(File.Exists(path + ".bak"));
    }
}
