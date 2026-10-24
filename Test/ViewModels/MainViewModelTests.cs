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

        Assert.Equal(ExtensionAddResult.Added, added);
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

        Assert.Equal(ExtensionAddResult.Duplicate, added);
        Assert.Equal(count, vm.ExtensionOptions.Count);
    }

    [Fact]
    public void TryAddExtension_RejectsInvalidInput()
    {
        var vm = CreateViewModel();
        var group = vm.ExtensionGroups.First();

        Assert.Equal(ExtensionAddResult.Invalid, vm.TryAddExtension(group, ""));
        Assert.Equal(ExtensionAddResult.Invalid, vm.TryAddExtension(group, "."));
        Assert.Equal(ExtensionAddResult.Invalid, vm.TryAddExtension(group, ".my ext"));
    }

    // ---- B2: a restored custom extension is a duplicate, not an invalid input ----

    [Fact]
    public void TryAddExtension_RestoredCustomExtension_IsReportedAsDuplicate()
    {
        var settingsPath = Path.Combine(_tempDir, "settings-dup.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(settingsPath,
            """{"CustomExtensions":[".log"],"SelectedExtensions":[".log",".cs"]}""");

        var vm = new MainViewModel(new AppSettingsStore(settingsPath));

        Assert.Equal(ExtensionAddResult.Duplicate, vm.TryAddExtension(vm.ExtensionGroups.First(), ".log"));
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

    // ---- D1: the temp backup can be deleted from the report page ----

    [Fact]
    public async Task DeleteBackup_RemovesDirectoryAndHidesNotice()
    {
        var dir = Path.Combine(_tempDir, "src7");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.txt"),
            Encoding.GetEncoding("GBK").GetBytes("你好世界"));

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        vm.IsOverwriteInPlace = true;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);

        var backup = vm.Summary!.BackupDirectory;
        Assert.NotNull(backup);
        Assert.True(Directory.Exists(backup));
        Assert.True(vm.CanDeleteBackup);

        vm.DeleteBackupCommand.Execute(null);

        Assert.False(Directory.Exists(backup));
        Assert.Null(vm.Summary!.BackupDirectory);
        Assert.False(vm.CanDeleteBackup);
    }

    [Fact]
    public async Task DeleteBackup_WithoutBackup_IsDisabled()
    {
        var dir = Path.Combine(_tempDir, "src8");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);

        // Non-in-place runs create no backup, so the command must not be executable.
        Assert.Null(vm.Summary!.BackupDirectory);
        Assert.False(vm.CanDeleteBackup);
    }

    [Fact]
    public void DeleteBackupCommand_RaisesCanExecuteChangedWhenSummaryChanges()
    {
        var vm = CreateViewModel();
        var canExecuteChanges = 0;
        vm.DeleteBackupCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        // No backup yet: disabled.
        Assert.False(vm.DeleteBackupCommand.CanExecute(null));

        var backupDir = Path.Combine(_tempDir, "backup9");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "a.txt"), "hello");

        // A finished in-place run publishes its backup location through Summary.
        vm.Summary = new ConversionSummary
        {
            Success = 1,
            OutputPath = _tempDir,
            BackupDirectory = backupDir
        };

        Assert.True(vm.CanDeleteBackup);
        Assert.True(vm.DeleteBackupCommand.CanExecute(null));
        // The command must be refreshed proactively; waiting for the next WPF
        // CommandManager requery would leave the button stale.
        Assert.True(canExecuteChanges >= 1, "setting Summary must raise CanExecuteChanged");

        vm.DeleteBackupCommand.Execute(null);

        Assert.False(Directory.Exists(backupDir));
        Assert.Null(vm.Summary!.BackupDirectory);
        Assert.False(vm.DeleteBackupCommand.CanExecute(null));
        Assert.True(canExecuteChanges >= 2, "deleting the backup must raise CanExecuteChanged");
    }

    // ---- B9: "rescan" drops every piece of scan residue, not just the list ----

    [Fact]
    public void Reset_ClearsScanResidue()
    {
        var vm = CreateViewModel();
        vm.IsOverwriteInPlace = true;
        vm.HasConverted = true;

        vm.ResetCommand.Execute(null);

        Assert.False(vm.IsOverwriteInPlace);
        Assert.False(vm.HasConverted);
        Assert.Null(vm.SelectedFileItem);
    }

    // ---- B1: choices survive a close without an intervening scan ----

    [Fact]
    public void SaveSettings_PersistsChoicesWithoutScanning()
    {
        var dir = Path.Combine(_tempDir, "src6");
        Directory.CreateDirectory(dir);
        var settingsPath = Path.Combine(_tempDir, "settings-save.json");

        var vm = new MainViewModel(new AppSettingsStore(settingsPath));
        vm.SourceFolderPath = dir;
        vm.SelectedEncoding = vm.AvailableEncodings.First(e => e.DisplayName == "GBK");
        vm.ExtensionOptions.First(o => o.Extension == ".md").IsSelected = false;

        // Wired to the window's Closing event.
        vm.SaveSettings();

        var reloaded = new MainViewModel(new AppSettingsStore(settingsPath));
        Assert.Equal(dir, reloaded.SourceFolderPath);
        Assert.Equal("GBK", reloaded.SelectedEncoding!.DisplayName);
        Assert.False(reloaded.ExtensionOptions.First(o => o.Extension == ".md").IsSelected);
    }

    // ---- A1: repeated in-place conversion must never use a stale detection ----

    [Fact]
    public async Task ConvertAsync_TwiceInPlace_DoesNotCorruptContent()
    {
        var dir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(dir);
        var gbk = Encoding.GetEncoding("GBK");
        // Long enough for Ude to detect reliably (see CLAUDE.md).
        var expected = "你好世界 Hello World 这是一段用于验证重复转换安全性的中文文本内容。";
        var path = Path.Combine(dir, "a.txt");
        File.WriteAllBytes(path, gbk.GetBytes(expected));

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        vm.SelectedEncoding = vm.AvailableEncodings.First(e => e.DisplayName.Contains("无 BOM"));
        vm.IsOverwriteInPlace = true;

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);
        Assert.Equal(expected, File.ReadAllText(path, new UTF8Encoding(false)));

        // A second run used to re-decode the already-UTF-8 bytes as GBK.
        await vm.ConvertCommand.ExecuteAsync(null);

        Assert.Equal(expected, File.ReadAllText(path, new UTF8Encoding(false)));
    }

    [Fact]
    public async Task ConvertAsync_InPlace_RefreshesDetectedEncoding()
    {
        var dir = Path.Combine(_tempDir, "src2");
        Directory.CreateDirectory(dir);
        var gbk = Encoding.GetEncoding("GBK");
        var text = "这是一段足够长的中文测试文本，用于让编码检测器能够可靠地识别出 UTF-8 编码。";
        File.WriteAllBytes(Path.Combine(dir, "a.txt"), gbk.GetBytes(text));

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        vm.SelectedEncoding = vm.AvailableEncodings.First(e => e.DisplayName.Contains("无 BOM"));
        vm.IsOverwriteInPlace = true;
        await vm.ScanCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.FileItems);
        Assert.NotEqual(EncodingDetector.Utf8NoBomName, item.DetectedEncoding);

        await vm.ConvertCommand.ExecuteAsync(null);

        // The file is now UTF-8; the stale scan-time name must be gone.
        Assert.Equal(EncodingDetector.Utf8NoBomName, item.DetectedEncoding);
    }

    [Fact]
    public async Task HasConverted_BlocksConvertUntilRescan()
    {
        var dir = Path.Combine(_tempDir, "src3");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.CanConvert);

        // Set when the post-conversion re-detection could not run: converting
        // again would decode with a stale encoding, so the button locks.
        vm.HasConverted = true;
        Assert.False(vm.CanConvert);

        vm.ResetCommand.Execute(null);
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.False(vm.HasConverted);
        Assert.True(vm.CanConvert);
    }

    // ---- B3: the progress bar must deterministically reach 100% ----

    [Fact]
    public async Task ConvertAsync_ProgressReachesMaximum()
    {
        var dir = Path.Combine(_tempDir, "src4");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "img.png"), new byte[] { 0x89, 0x50 }); // unmatched

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);

        await WaitForProgressAsync(() => vm.ProgressValue, vm.ProgressMaximum);
    }

    // ---- B4: backup failures shrink the conversion list; the bar still completes ----

    [Fact]
    public async Task ConvertAsync_InPlaceWithBackupFailure_ProgressReachesMaximum()
    {
        var dir = Path.Combine(_tempDir, "src5");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "locked.txt");
        File.WriteAllText(path, "hello");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        vm.IsOverwriteInPlace = true;
        await vm.ScanCommand.ExecuteAsync(null);

        // Hold the file exclusively so the backup pass cannot copy it and the item
        // is dropped from the conversion list.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.ConvertCommand.ExecuteAsync(null);
        }

        await WaitForProgressAsync(() => vm.ProgressValue, vm.ProgressMaximum);
    }

    // ---- 命令可用性（CanExecute）逻辑 ----

    [Fact]
    public void CanScan_RequiresFolderPathAndIdle()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CanScan);

        vm.SourceFolderPath = _tempDir;
        Assert.True(vm.CanScan);

        vm.IsScanning = true;
        Assert.False(vm.CanScan);
        vm.IsScanning = false;

        vm.IsConverting = true;
        Assert.False(vm.CanScan);
    }

    [Fact]
    public void CanConvert_RequiresItemsAndNotLocked()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CanConvert);

        vm.FileItems.Add(new FileConversionItem());
        Assert.True(vm.CanConvert);

        vm.HasConverted = true;
        Assert.False(vm.CanConvert);
        vm.HasConverted = false;

        vm.IsConverting = true;
        Assert.False(vm.CanConvert);
    }

    [Fact]
    public void CanCancel_OnlyWhileConverting()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CanCancel);

        vm.IsConverting = true;
        Assert.True(vm.CanCancel);
    }

    [Fact]
    public void CanCheckForUpdates_BlockedWhileConverting()
    {
        var vm = CreateViewModel();

        Assert.True(vm.CanCheckForUpdates);

        // Replacing binaries mid-conversion would restart the app halfway through.
        vm.IsConverting = true;
        Assert.False(vm.CanCheckForUpdates);

        vm.IsConverting = false;
        vm.IsCheckingUpdates = true;
        Assert.False(vm.CanCheckForUpdates);

        // A running download doubles as the cancel action and stays available.
        vm.IsDownloading = true;
        Assert.True(vm.CanCheckForUpdates);
    }

    [Fact]
    public async Task ConvertAsync_ReportExposesRuleSkippedFolders()
    {
        var dir = Path.Combine(_tempDir, "src-notcopied");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "x.js"), "js");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);

        var section = Assert.Single(
            vm.ReportSections, s => s.Title == Encodex.Resources.Res.VM_NotCopiedSection);
        Assert.Contains("node_modules", section.Files);
    }

    [Fact]
    public void BackupDirectoryText_ReflectsSummaryBackup()
    {
        var vm = CreateViewModel();

        Assert.Null(vm.BackupDirectoryText);

        vm.Summary = new ConversionSummary { Success = 1, BackupDirectory = @"C:\temp\backup" };
        Assert.NotNull(vm.BackupDirectoryText);
        Assert.Contains(@"C:\temp\backup", vm.BackupDirectoryText);
    }

    // ---- 扫描/转换流程的更多边界 ----

    [Fact]
    public async Task ScanAsync_NonexistentFolder_KeepsListEmpty()
    {
        var vm = CreateViewModel();
        vm.SourceFolderPath = Path.Combine(_tempDir, "no-such-folder");

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Empty(vm.FileItems);
    }

    [Fact]
    public async Task ScanAsync_UnreadableFile_StaysInListWithUnknownEncoding()
    {
        // Regression: one unreadable file used to abort the whole scan; it must be
        // degraded to "unknown" (copied as-is later) without affecting the rest.
        var dir = Path.Combine(_tempDir, "src-unreadable");
        Directory.CreateDirectory(dir);
        var locked = Path.Combine(dir, "secret.txt");
        File.WriteAllText(locked, "content that cannot be read back");
        File.WriteAllText(Path.Combine(dir, "normal.txt"), "perfectly readable");
        DenyRead(locked);

        try
        {
            var vm = CreateViewModel();
            vm.SourceFolderPath = dir;
            await vm.ScanCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.FileItems.Count);
            var lockedItem = vm.FileItems.First(i => i.FileName == "secret.txt");
            Assert.Null(lockedItem.DetectedEncoding);
            Assert.Equal(ConversionStatus.Pending, lockedItem.Status);
        }
        finally
        {
            AllowRead(locked);
        }
    }

    [Fact]
    public async Task ConvertAsync_SkipsUncheckedItems()
    {
        var dir = Path.Combine(_tempDir, "src-unchecked");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "convert me");
        File.WriteAllText(Path.Combine(dir, "skip.txt"), "leave me alone");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        vm.FileItems.First(i => i.FileName == "skip.txt").IsSelected = false;

        await vm.ConvertCommand.ExecuteAsync(null);

        var outputDir = vm.Summary!.OutputPath;
        Assert.True(File.Exists(Path.Combine(outputDir, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(outputDir, "skip.txt")));
        // The unchecked item was never touched by the conversion pass.
        Assert.Equal(ConversionStatus.Pending,
            vm.FileItems.First(i => i.FileName == "skip.txt").Status);
    }

    [Fact]
    public async Task ConvertAsync_CopiesUnmatchedFilesIntoOutputTree()
    {
        var dir = Path.Combine(_tempDir, "src-unmatched");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "img.png"), new byte[] { 0x89, 0x50 });

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ConvertCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(vm.Summary!.OutputPath, "img.png")));
    }

    [Fact]
    public async Task ScannedCountText_ReflectsItemCount()
    {
        var dir = Path.Combine(_tempDir, "src-count");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "two");

        var vm = CreateViewModel();
        vm.SourceFolderPath = dir;
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Encodex.Resources.Res.Ui_ScannedCount, 2), vm.ScannedCountText);
    }

    // ---- 编码候选名与解析器的同步 ----

    [Fact]
    public void AvailableDetectedEncodings_AllResolveThroughDetector()
    {
        // The DataGrid offers these names for manual correction; every one must be
        // convertible back into an Encoding or a corrected file would fail to convert.
        var vm = CreateViewModel();

        Assert.All(vm.AvailableDetectedEncodings,
            name => Assert.NotNull(EncodingDetector.ResolveName(name)));
    }

    // ---- 设置恢复的更多分支 ----

    [Fact]
    public void Constructor_RestoresCustomExtensionsIntoCustomGroup()
    {
        var settingsPath = Path.Combine(_tempDir, "settings-custom.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(settingsPath,
            """{"CustomExtensions":[".log",".cfg"],"SelectedExtensions":[".log"]}""");

        var vm = new MainViewModel(new AppSettingsStore(settingsPath));

        // .cfg is a default extension and must not be duplicated; .log lands in
        // the custom group with its saved check state applied.
        Assert.Equal(1, vm.ExtensionOptions.Count(o => o.Extension == ".cfg"));
        var customGroup = vm.ExtensionGroups.Last();
        Assert.Single(customGroup.Extensions);
        Assert.Equal(".log", customGroup.Extensions[0].Extension);
        Assert.True(customGroup.Extensions[0].IsSelected);
    }

    [Fact]
    public void ToggleTheme_PersistsThemeChoice()
    {
        var settingsPath = Path.Combine(_tempDir, "settings-theme.json");
        var vm = new MainViewModel(new AppSettingsStore(settingsPath));
        var initial = vm.IsLightTheme;

        vm.ToggleThemeCommand.Execute(null);

        var reloaded = new MainViewModel(new AppSettingsStore(settingsPath));
        Assert.Equal(!initial, reloaded.IsLightTheme);
    }

    private static void DenyRead(string path)
    {
        var security = File.GetAccessControl(path);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Deny));
        File.SetAccessControl(path, security);
    }

    private static void AllowRead(string path)
    {
        var security = File.GetAccessControl(path);
        security.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Deny));
        File.SetAccessControl(path, security);
    }

    /// <summary>Progress&lt;T&gt; posts callbacks asynchronously when no SynchronizationContext
    /// is captured (as under xUnit), so the terminal value can land just after the command
    /// completes. Wait for it; without the terminal correction the value never arrives.</summary>
    private static async Task WaitForProgressAsync(Func<int> value, int expected)
    {
        for (var attempt = 0; attempt < 200 && value() != expected; attempt++)
            await Task.Delay(10);
        Assert.Equal(expected, value());
    }

    private MainViewModel CreateViewModel()
        => new(new AppSettingsStore(Path.Combine(_tempDir, "settings.json")));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
