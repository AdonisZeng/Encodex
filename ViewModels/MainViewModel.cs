using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Encodex.Models;
using Encodex.Services;

namespace Encodex.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private readonly EncodingDetector _detector = new();
    private readonly EncodingConverter _converter = new();
    private readonly ExtensionProfile _extensionProfile = new();
    private readonly AppUpdateService _updateService = new();
    private CancellationTokenSource? _cts;
    private List<string> _unmatchedFiles = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _sourceFolderPath = "";

    [ObservableProperty]
    private EncodingOption? _selectedEncoding;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIcon))]
    private bool _isLightTheme;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isConverting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private int _progressMaximum = 100;

    [ObservableProperty]
    private string _currentFileName = "";

    [ObservableProperty]
    private string _statusText = "请选择项目文件夹";

    [ObservableProperty]
    private ConversionSummary? _summary;

    public ObservableCollection<EncodingOption> AvailableEncodings { get; }
    public ObservableCollection<ExtensionOption> ExtensionOptions { get; }
    public ObservableCollection<ExtensionGroup> ExtensionGroups { get; } = new();
    public ObservableCollection<ReportSection> ReportSections { get; } = new();
    public ObservableCollection<FileConversionItem> FileItems { get; } = new();

    /// <summary>Icon shown on the theme toggle button: the theme the button switches to.</summary>
    public string ThemeIcon => IsLightTheme ? "🌙" : "☀️";

    public MainViewModel()
        : this(new AppSettingsStore())
    {
    }

    internal MainViewModel(AppSettingsStore settingsStore)
    {
        AvailableEncodings = new ObservableCollection<EncodingOption>(EncodingOption.GetDefaultEncodings());
        SelectedEncoding = AvailableEncodings.First();

        // Build the grouped view; ExtensionOptions keeps a flat view of the very
        // same option instances for snapshotting and validation.
        ExtensionOptions = new ObservableCollection<ExtensionOption>();
        foreach (var (name, extensions) in ExtensionProfile.GetDefaultGroups())
        {
            var options = extensions.Select(e => new ExtensionOption(e)).ToList();
            foreach (var option in options)
                ExtensionOptions.Add(option);
            ExtensionGroups.Add(new ExtensionGroup(name, options));
        }

        IsLightTheme = settingsStore.Load().IsLightTheme;
    }

    public bool CanScan => !string.IsNullOrEmpty(SourceFolderPath) && !IsConverting && !IsScanning;
    public bool CanConvert => FileItems.Count > 0 && !IsConverting;
    public bool CanCancel => IsConverting;
    public bool CanCheckForUpdates => !IsCheckingUpdates;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var path = SourceFolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText = "请选择有效的文件夹";
            return;
        }

        // An empty profile would match nothing and make the scanner copy the whole
        // folder tree as unmatched files — almost certainly not what the user wants.
        if (!ExtensionOptions.Any(o => o.IsSelected))
        {
            StatusText = "请至少勾选一个文件扩展名";
            return;
        }

        IsScanning = true;
        FileItems.Clear();
        StatusText = "正在扫描...";

        try
        {
            // Snapshot the profile and encoding before leaving the UI thread: the user
            // can still edit extensions or the target encoding while the scan runs.
            var profile = SnapshotProfile();
            var targetEncodingName = SelectedEncoding!.DisplayName;

            // Scanning and per-file detection are CPU/IO heavy: keep them off the UI thread.
            var (matched, unmatched, items) = await Task.Run(() =>
            {
                var (m, u) = _scanner.ScanAll(path, profile);
                var list = new List<FileConversionItem>();
                foreach (var file in m)
                {
                    var detection = _detector.Detect(file.FullPath);
                    list.Add(new FileConversionItem
                    {
                        RelativePath = file.RelativePath,
                        FileName = file.FileName,
                        FileSize = file.FileSize,
                        DetectedEncoding = detection.EncodingName,
                        TargetEncoding = targetEncodingName,
                        Status = detection.IsBinary ? ConversionStatus.Skipped : ConversionStatus.Pending,
                        StatusMessage = detection.IsBinary ? "二进制文件" : null
                    });
                }
                return (m, u, list);
            });

            _unmatchedFiles = unmatched;
            foreach (var item in items)
                FileItems.Add(item);

            SelectedTabIndex = 1;
            StatusText = $"已扫描 {FileItems.Count} 个文件";
            ConvertCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    // Snapshot copy of the extension profile so a background scan never reads the
    // live ObservableCollection concurrently with UI edits. Only checked extensions
    // participate in the scan.
    private ExtensionProfile SnapshotProfile()
    {
        var copy = new ExtensionProfile(loadDefaults: false);
        foreach (var option in ExtensionOptions.Where(o => o.IsSelected))
            copy.AddExtension(option.Extension);
        return copy;
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        IsConverting = true;
        _cts = new CancellationTokenSource();
        SelectedTabIndex = 2;
        StatusText = "正在转换...";

        try
        {
            var sourceFolderName = Path.GetFileName(SourceFolderPath);
            var encodingName = SelectedEncoding!.Encoding.WebName;
            var outputDir = Path.Combine(
                Path.GetDirectoryName(SourceFolderPath)!,
                $"{sourceFolderName}_{encodingName}");

            // Handle name collision
            int suffix = 2;
            while (Directory.Exists(outputDir))
            {
                outputDir = Path.Combine(
                    Path.GetDirectoryName(SourceFolderPath)!,
                    $"{sourceFolderName}_{encodingName}_{suffix}");
                suffix++;
            }

            Directory.CreateDirectory(outputDir);

            var selectedItems = FileItems.Where(f => f.IsSelected).ToList();
            ProgressMaximum = selectedItems.Count + _unmatchedFiles.Count;
            ProgressValue = 0;

            // Snapshot inputs used inside the background delegates: the user is not
            // prevented from changing the target encoding or picking a new folder
            // while a conversion runs.
            var sourcePath = SourceFolderPath;
            var encoding = SelectedEncoding!.Encoding;
            var unmatchedFiles = _unmatchedFiles;

            var progress = new Progress<ConversionProgress>(p =>
            {
                ProgressValue = p.Processed;
                CurrentFileName = p.CurrentFile;
            });

            // Run on the thread pool: conversion and copying contain synchronous
            // File.Copy/Move calls that would otherwise block the UI thread.
            // Progress<T> was created on the UI thread, so its callbacks still
            // marshal back to the UI thread.
            var summary = await Task.Run(() => _converter.ConvertAsync(
                selectedItems,
                sourcePath,
                outputDir,
                encoding,
                progress,
                _cts.Token), _cts.Token);

            // Copy unmatched files; progress continues from where conversion stopped.
            var copyResult = await Task.Run(() => _converter.CopyUnmatchedFilesAsync(
                sourcePath,
                outputDir,
                unmatchedFiles,
                progress,
                selectedItems.Count,
                _cts.Token), _cts.Token);

            Summary = new ConversionSummary
            {
                Success = summary.Success,
                Skipped = summary.Skipped,
                Failed = summary.Failed + copyResult.Failed,
                Copied = summary.Copied + copyResult.Copied,
                OutputPath = outputDir
            };
            BuildReportSections(copyResult);

            SelectedTabIndex = 3;
            StatusText = $"转换完成: 成功 {summary.Success}, 跳过 {summary.Skipped}, 失败 {summary.Failed + copyResult.Failed}, 复制 {summary.Copied + copyResult.Copied}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "转换已取消";
        }
        finally
        {
            IsConverting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        // .NET Framework has no Microsoft.Win32.OpenFolderDialog (.NET 8+ only),
        // so the WinForms folder browser is used instead.
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择项目文件夹"
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SourceFolderPath = dialog.SelectedPath;
            StatusText = $"已选择: {SourceFolderPath}";
        }
    }

    [RelayCommand]
    private void AddExtension(ExtensionGroup? group)
    {
        if (group == null)
            return;

        var dialog = new AddExtensionDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ExtensionInput))
            return;

        if (!TryAddExtension(group, dialog.ExtensionInput))
        {
            MessageBox.Show(
                $"扩展名 \"{dialog.ExtensionInput.Trim()}\" 无效或已存在。",
                "添加扩展名", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Adds a custom extension to the profile and to the given group.</summary>
    public bool TryAddExtension(ExtensionGroup group, string input)
    {
        if (!_extensionProfile.AddExtension(input, out var normalized))
            return false;

        var option = new ExtensionOption(normalized);
        ExtensionOptions.Add(option);
        group.Extensions.Add(option);
        return true;
    }

    /// <summary>Builds the expandable report sections with concrete file lists.
    /// Status semantics match the summary counters: "原样复制" items are Copied.</summary>
    private void BuildReportSections(UnmatchedCopyResult copyResult)
    {
        static string Entry(FileConversionItem item)
            => string.IsNullOrEmpty(item.StatusMessage)
                ? item.RelativePath
                : $"{item.RelativePath}（{item.StatusMessage}）";

        var success = FileItems.Where(i => i.Status == ConversionStatus.Success)
            .Select(Entry).ToList();
        var skipped = FileItems.Where(i => i.Status == ConversionStatus.Skipped)
            .Select(Entry).ToList();
        var failed = FileItems.Where(i => i.Status == ConversionStatus.Failed)
            .Select(Entry)
            .Concat(copyResult.FailedFiles.Select(p => $"{p}（复制失败）"))
            .ToList();
        var copied = FileItems.Where(i => i.Status == ConversionStatus.Copied)
            .Select(Entry)
            .Concat(copyResult.CopiedFiles)
            .ToList();

        ReportSections.Clear();
        ReportSections.Add(new ReportSection("✅", "成功转换", success));
        ReportSections.Add(new ReportSection("⏭️", "跳过", skipped));
        ReportSections.Add(new ReportSection("❌", "失败", failed));
        ReportSections.Add(new ReportSection("📄", "复制", copied));
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsLightTheme = !IsLightTheme;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (Summary?.OutputPath != null && Directory.Exists(Summary.OutputPath))
        {
            // Quote the path: explorer.exe splits unquoted arguments on spaces.
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Summary.OutputPath}\"");
        }
    }

    [RelayCommand]
    private void Reset()
    {
        FileItems.Clear();
        Summary = null;
        ReportSections.Clear();
        ProgressValue = 0;
        CurrentFileName = "";
        SelectedTabIndex = 0;
        StatusText = "请选择项目文件夹";
        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdates = true;
        StatusText = "正在检查更新...";
        try
        {
            var update = await _updateService.CheckAsync();
            if (update == null)
            {
                StatusText = $"当前已是最新版本 (v{AppUpdateService.CurrentVersion})";
                MessageBox.Show(
                    $"当前已是最新版本 (v{AppUpdateService.CurrentVersion})。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : $"\n\n更新内容:\n{update.Notes}";
            var answer = MessageBox.Show(
                $"发现新版本 v{update.Version}（当前 v{AppUpdateService.CurrentVersion}）{notes}\n\n是否立即下载并安装？",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = "已取消更新";
                return;
            }

            StatusText = "正在下载更新包...";
            var zipPath = await _updateService.DownloadAsync(update);

            // From here on there is no going back: the updater replaces the files
            // and restarts the app, so warn the user before shutting down.
            var confirm = MessageBox.Show(
                "更新包已下载完成。\n应用即将关闭并自动完成更新，请确认当前没有进行中的转换。",
                "准备更新", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                StatusText = "已取消更新";
                return;
            }

            StatusText = "正在应用更新...";
            _updateService.ApplyUpdateAndExit(zipPath);
        }
        catch (UpdateException ex)
        {
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText = $"检查更新失败: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }
}
