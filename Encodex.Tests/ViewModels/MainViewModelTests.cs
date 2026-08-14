using System.IO;
using System.Text;
using Encodex.Models;
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
    public void Constructor_GroupsCoverAllDefaultExtensions()
    {
        var vm = CreateViewModel();

        Assert.True(vm.ExtensionGroups.Count >= 3);
        Assert.All(vm.ExtensionGroups, g => Assert.NotEmpty(g.Extensions));
        Assert.Equal(
            ExtensionProfile.GetDefaultExtensions().Length,
            vm.ExtensionGroups.Sum(g => g.Extensions.Count));
        // No extension appears in more than one group.
        var flattened = vm.ExtensionGroups.SelectMany(g => g.Extensions).ToList();
        Assert.Equal(flattened.Count, flattened.Select(o => o.Extension).Distinct().Count());
    }

    [Fact]
    public void TryAddExtension_AddsSelectedOptionToGroup()
    {
        var vm = CreateViewModel();
        var group = vm.ExtensionGroups.First();

        var added = vm.TryAddExtension(group, ".log");

        Assert.True(added);
        var option = group.Extensions.Last();
        Assert.Equal(".log", option.Extension);
        Assert.True(option.IsSelected);
        Assert.Contains(vm.ExtensionOptions, o => o.Extension == ".log");
    }

    [Fact]
    public void TryAddExtension_RejectsDuplicatesAcrossGroups()
    {
        var vm = CreateViewModel();
        var group = vm.ExtensionGroups.Last();

        var count = vm.ExtensionOptions.Count;
        var added = vm.TryAddExtension(group, ".cs");

        Assert.False(added);
        Assert.Equal(count, vm.ExtensionOptions.Count);
    }

    [Fact]
    public void TryAddExtension_RejectsInvalidInput()
    {
        var vm = CreateViewModel();
        var group = vm.ExtensionGroups.First();

        Assert.False(vm.TryAddExtension(group, ""));
        Assert.False(vm.TryAddExtension(group, "."));
        Assert.False(vm.TryAddExtension(group, ".my ext"));
    }

    [Fact]
    public void TryAddExtension_UncheckedOptionStaysInList()
    {
        var vm = CreateViewModel();
        var group = vm.ExtensionGroups.First();
        vm.TryAddExtension(group, ".log");

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

    [Fact]
    public async Task ConvertAsync_BuildsReportSections_MatchingSummaryCounts()
    {
        var dir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "b.txt"),
            Encoding.GetEncoding("GBK").GetBytes("你好"));
        // Unmatched extension: copied as-is, shows up in the 复制 section.
        File.WriteAllBytes(Path.Combine(dir, "img.png"), new byte[] { 0x89, 0x50 });

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Summary);
        Assert.Equal(4, vm.ReportSections.Count);
        Assert.Equal("成功转换", vm.ReportSections[0].Title);
        Assert.Equal("跳过", vm.ReportSections[1].Title);
        Assert.Equal("失败", vm.ReportSections[2].Title);
        Assert.Equal("复制", vm.ReportSections[3].Title);

        foreach (var section in vm.ReportSections)
            Assert.Equal(section.Files.Count, section.Count);
        Assert.Equal(vm.Summary!.Success, vm.ReportSections[0].Count);
        Assert.Equal(vm.Summary.Skipped, vm.ReportSections[1].Count);
        Assert.Equal(vm.Summary.Failed, vm.ReportSections[2].Count);
        Assert.Equal(vm.Summary.Copied, vm.ReportSections[3].Count);
        Assert.Contains(vm.ReportSections[3].Files, f => f.Contains("img.png"));
    }

    [Fact]
    public async Task ConvertAsync_Utf8WithBom_OutputFolderIsDistinct()
    {
        var dir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        vm.SelectedEncoding = vm.AvailableEncodings.First(e => e.DisplayName.Contains("带 BOM"));

        await vm.ConvertCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Summary);
        Assert.EndsWith("_utf-8-bom", vm.Summary!.OutputPath);
    }

    [Fact]
    public async Task ScanAsync_PersistsFolderEncodingAndExtensions()
    {
        var dir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.cs"), "int x = 1;");

        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var vm = new MainViewModel(new AppSettingsStore(settingsPath));
        vm.SourceFolderPath = dir;
        vm.SelectedEncoding = vm.AvailableEncodings.First(e => e.DisplayName == "GBK");
        vm.ExtensionOptions.First(o => o.Extension == ".md").IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        // A fresh view model over the same settings file restores the choices.
        var vm2 = new MainViewModel(new AppSettingsStore(settingsPath));
        Assert.Equal(dir, vm2.SourceFolderPath);
        Assert.Equal("GBK", vm2.SelectedEncoding!.DisplayName);
        Assert.False(vm2.ExtensionOptions.First(o => o.Extension == ".md").IsSelected);
    }

    [Fact]
    public void Reset_ClearsReportSections()
    {
        var vm = CreateViewModel();
        vm.Summary = new ConversionSummary { Success = 1 };

        vm.ResetCommand.Execute(null);

        Assert.Empty(vm.ReportSections);
        Assert.Null(vm.Summary);
    }

    private MainViewModel CreateViewModel()
        => new(new AppSettingsStore(Path.Combine(_tempDir, "settings.json")));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
