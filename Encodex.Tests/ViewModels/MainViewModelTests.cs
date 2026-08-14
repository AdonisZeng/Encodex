using System.IO;
using Encodex.Services;
using Encodex.ViewModels;
using Xunit;

namespace Encodex.Tests.ViewModels;

public class MainViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "encodex-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_LoadsDefaultExtensions_AllSelected()
    {
        var vm = CreateViewModel();

        Assert.Equal(ExtensionProfile.GetDefaultExtensions().Length, vm.ExtensionOptions.Count);
        Assert.All(vm.ExtensionOptions, o => Assert.True(o.IsSelected));
    }

    [Fact]
    public void AddExtension_AddsSelectedOptionAndClearsInput()
    {
        var vm = CreateViewModel();
        vm.NewExtensionInput = ".log";

        vm.AddExtensionCommand.Execute(null);

        var added = vm.ExtensionOptions.Last();
        Assert.Equal(".log", added.Extension);
        Assert.True(added.IsSelected);
        Assert.Equal("", vm.NewExtensionInput);
    }

    [Fact]
    public void AddExtension_RejectsDuplicates()
    {
        var vm = CreateViewModel();
        vm.NewExtensionInput = ".cs";

        var count = vm.ExtensionOptions.Count;
        vm.AddExtensionCommand.Execute(null);

        Assert.Equal(count, vm.ExtensionOptions.Count);
    }

    [Fact]
    public void AddExtension_UncheckedOptionStaysInList()
    {
        var vm = CreateViewModel();
        vm.NewExtensionInput = ".log";
        vm.AddExtensionCommand.Execute(null);

        var added = vm.ExtensionOptions.Last();
        added.IsSelected = false;

        // Unchecking a custom extension keeps the entry so it can be re-checked later.
        Assert.Contains(vm.ExtensionOptions, o => o.Extension == ".log" && !o.IsSelected);
    }

    [Fact]
    public void ToggleTheme_FlipsIsLightTheme()
    {
        var vm = CreateViewModel();
        var initial = vm.IsLightTheme;

        vm.ToggleThemeCommand.Execute(null);

        Assert.NotEqual(initial, vm.IsLightTheme);
    }

    [Fact]
    public void Constructor_RestoresPersistedTheme()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(settingsPath, """{"IsLightTheme":true}""");

        var vm = new MainViewModel(new AppSettingsStore(settingsPath));

        Assert.True(vm.IsLightTheme);
    }

    [Fact]
    public void Constructor_DefaultsToDarkTheme_WhenNoSettingsExist()
    {
        var vm = CreateViewModel();

        Assert.False(vm.IsLightTheme);
    }

    [Fact]
    public async Task ScanAsync_OnlyIncludesCheckedExtensions()
    {
        var dir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.cs"), "int x = 1;");
        File.WriteAllText(Path.Combine(dir, "b.log"), "just a log line");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        vm.ExtensionOptions.First(o => o.Extension == ".txt").IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.FileItems);
        Assert.Equal("a.cs", item.RelativePath);
    }

    [Fact]
    public async Task ScanAsync_BlocksWhenNoExtensionIsChecked()
    {
        var dir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.cs"), "int x = 1;");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        foreach (var option in vm.ExtensionOptions)
            option.IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(vm.FileItems);
        Assert.Contains("至少勾选", vm.StatusText);
    }

    private MainViewModel CreateViewModel()
        => new(new AppSettingsStore(Path.Combine(_tempDir, "settings.json")));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
