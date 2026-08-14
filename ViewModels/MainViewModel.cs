using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Encodex.Models;
using Encodex.Resources;
using Encodex.Services;
using Wpf.Ui.Appearance;

namespace Encodex.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FileScanner _scanner = new();
    private readonly EncodingDetector _detector = new();
    private readonly EncodingConverter _converter = new();
    private readonly ExtensionProfile _extensionProfile = new();
    private readonly AppUpdateService _updateService = new();
    private readonly AppSettingsStore _settingsStore;
    private CancellationTokenSource? _cts;
    private List<string> _unmatchedFiles = new();

    /// <summary>Candidates for manually correcting a file's detected encoding.</summary>
    public string[] AvailableDetectedEncodings { get; } =
    {
        "utf-8", "utf-16LE", "utf-16BE", "utf-32LE", "utf-32BE",
        "GBK", "GB2312", "GB18030", "Big5", "shift_jis", "euc-kr",
        "us-ascii", "iso-8859-1"
    };

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
    private string _statusText = Res.VM_SelectFolder;

    /// <summary>In-place conversion: rewrite source files (backed up to %TEMP% first).</summary>
    [ObservableProperty]
    private bool _isOverwriteInPlace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewSelectedCommand))]
    private FileConversionItem? _selectedFileItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupDirectoryText))]
    private ConversionSummary? _summary;

    public ObservableCollection<EncodingOption> AvailableEncodings { get; }
    public ObservableCollection<ExtensionOption> ExtensionOptions { get; }
    public ObservableCollection<ExtensionGroup> ExtensionGroups { get; } = new();
    public ObservableCollection<ReportSection> ReportSections { get; } = new();
    public ObservableCollection<FileConversionItem> FileItems { get; } = new();

    /// <summary>Icon shown on the theme toggle button: the theme the button switches to.</summary>
    public string ThemeIcon => IsLightTheme ? "🌙" : "☀️";

    /// <summary>Header text above the file list ("已扫描 N 个文件").</summary>
    public string ScannedCountText => string.Format(Res.Ui_ScannedCount, FileItems.Count);

    /// <summary>Backup location shown in the report after an in-place conversion.</summary>
    public string? BackupDirectoryText
        => Summary?.BackupDirectory is { Length: > 0 } backup
            ? string.Format(Res.VM_BackupDir, backup)
            : null;

    public MainViewModel()
        : this(new AppSettingsStore())
    {
    }

    internal MainViewModel(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;

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

        RestoreSettings();
    }

    public bool CanScan => !string.IsNullOrEmpty(SourceFolderPath) && !IsConverting && !IsScanning;
    public bool CanConvert => FileItems.Count > 0 && !IsConverting;
    public bool CanCancel => IsConverting;
    public bool CanCheckForUpdates => !IsCheckingUpdates;
    public bool CanPreview => SelectedFileItem != null;

    /// <summary>Restores the persisted folder / encoding / extension selection.</summary>
    private void RestoreSettings()
    {
        var settings = _settingsStore.Load();
        IsLightTheme = settings.IsLightTheme;
        SourceFolderPath = settings.SourceFolderPath ?? "";

        if (settings.SelectedEncoding != null)
        {
            var match = AvailableEncodings.FirstOrDefault(e => e.DisplayName == settings.SelectedEncoding);
            if (match != null)
                SelectedEncoding = match;
        }

        // Only apply the saved selection when it was actually persisted (an empty
        // list means "not saved yet" and must not uncheck everything).
        if (settings.SelectedExtensions.Count > 0)
        {
            foreach (var option in ExtensionOptions)
                option.IsSelected = settings.SelectedExtensions.Contains(option.Extension);
        }
    }

    /// <summary>Persists the current configuration so the next launch restores it.</summary>
    private void SaveSettings()
    {
        _settingsStore.Save(new AppSettings
        {
            IsLightTheme = IsLightTheme,
            SourceFolderPath = SourceFolderPath,
            SelectedEncoding = SelectedEncoding?.DisplayName,
            SelectedExtensions = ExtensionOptions.Where(o => o.IsSelected).Select(o => o.Extension).ToList()
        });
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var path = SourceFolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText = Res.VM_SelectValidFolder;
            return;
        }

        // An empty profile would match nothing and make the scanner copy the whole
        // folder tree as unmatched files — almost certainly not what the user wants.
        if (!ExtensionOptions.Any(o => o.IsSelected))
        {
            StatusText = Res.VM_SelectExtension;
            return;
        }

        SaveSettings();

        IsScanning = true;
        FileItems.Clear();
        StatusText = Res.VM_Scanning;

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
                        StatusMessage = detection.IsBinary ? Res.VM_Binary : null
                    });
                }
                return (m, u, list);
            });

            _unmatchedFiles = unmatched;
            foreach (var item in items)
                FileItems.Add(item);

            OnPropertyChanged(nameof(ScannedCountText));
            SelectedTabIndex = 1;
            StatusText = string.Format(Res.VM_Scanned, FileItems.Count);
            ConvertCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Res.VM_ScanFailed, ex.Message);
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
        StatusText = Res.VM_Converting;
        string outputDir = "";

        try
        {
            var sourceFolderName = Path.GetFileName(SourceFolderPath);
            var encodingName = GetEncodingFolderName(SelectedEncoding!.Encoding);

            if (IsOverwriteInPlace)
            {
                // In-place mode rewrites the source files; the backup lives in %TEMP%.
                outputDir = SourceFolderPath;
            }
            else
            {
                outputDir = Path.Combine(
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
            }

            var selectedItems = FileItems.Where(f => f.IsSelected).ToList();
            ProgressMaximum = selectedItems.Count + (IsOverwriteInPlace ? 0 : _unmatchedFiles.Count);
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
                _cts.Token,
                IsOverwriteInPlace), _cts.Token);

            // Copy unmatched files; progress continues from where conversion stopped.
            // In-place mode has nothing to copy: the unmatched files already live in
            // the source folder.
            UnmatchedCopyResult copyResult = new();
            if (!IsOverwriteInPlace)
            {
                copyResult = await Task.Run(() => _converter.CopyUnmatchedFilesAsync(
                    sourcePath,
                    outputDir,
                    unmatchedFiles,
                    progress,
                    selectedItems.Count,
                    _cts.Token), _cts.Token);
            }

            Summary = new ConversionSummary
            {
                Success = summary.Success,
                Skipped = summary.Skipped,
                Failed = summary.Failed + copyResult.Failed,
                Copied = summary.Copied + copyResult.Copied,
                OutputPath = summary.OutputPath,
                BackupDirectory = summary.BackupDirectory
            };
            BuildReportSections(copyResult);

            SelectedTabIndex = 3;
            StatusText = string.Format(Res.VM_ConvertDone,
                summary.Success, summary.Skipped, summary.Failed + copyResult.Failed, summary.Copied + copyResult.Copied);
        }
        catch (OperationCanceledException)
        {
            StatusText = Res.VM_ConvertCancelled;
            BuildPartialReport(outputDir);
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

    /// <summary>Opens the preview window for the selected file: source-decoded text
    /// and what the target encoding would produce, so mistakes surface before
    /// conversion instead of after.</summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private void PreviewSelected()
    {
        var item = SelectedFileItem;
        if (item == null)
            return;

        var window = new PreviewWindow(
            Path.Combine(SourceFolderPath, item.RelativePath),
            item.DetectedEncoding,
            SelectedEncoding!.Encoding)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
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
            StatusText = string.Format(Res.VM_Selected, SourceFolderPath);
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
                string.Format(Res.VM_InvalidExtension, dialog.ExtensionInput.Trim()),
                Res.VM_AddExtensionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
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
            .Concat(copyResult.FailedFiles.Select(p => $"{p}{Res.VM_CopyFailed}"))
            .ToList();
        var copied = FileItems.Where(i => i.Status == ConversionStatus.Copied)
            .Select(Entry)
            .Concat(copyResult.CopiedFiles)
            .ToList();

        ReportSections.Clear();
        ReportSections.Add(new ReportSection("✅", Res.VM_SuccessSection, success));
        ReportSections.Add(new ReportSection("⏭️", Res.VM_SkippedSection, skipped));
        ReportSections.Add(new ReportSection("❌", Res.VM_FailedSection, failed));
        ReportSections.Add(new ReportSection("📄", Res.VM_CopiedSection, copied));
    }

    /// <summary>Folder name for the output directory; distinguishes BOM variants that
    /// share the same WebName (UTF-8 with/without BOM are both "utf-8").</summary>
    private static string GetEncodingFolderName(Encoding encoding)
        => encoding.GetPreamble().Length > 0 ? encoding.WebName + "-bom" : encoding.WebName;

    /// <summary>Builds a best-effort report from files processed before a cancellation,
    /// so partial results are shown instead of being discarded.</summary>
    private void BuildPartialReport(string outputDir)
    {
        foreach (var item in FileItems.Where(i => i.Status == ConversionStatus.Pending))
            item.Status = ConversionStatus.Cancelled;

        Summary = new ConversionSummary
        {
            Success = FileItems.Count(i => i.Status == ConversionStatus.Success),
            Skipped = FileItems.Count(i => i.Status == ConversionStatus.Skipped),
            Failed = FileItems.Count(i => i.Status == ConversionStatus.Failed),
            Copied = FileItems.Count(i => i.Status == ConversionStatus.Copied),
            OutputPath = outputDir
        };
        BuildReportSections(new UnmatchedCopyResult());
        SelectedTabIndex = 3;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsLightTheme = !IsLightTheme;
        // Keep the window's default backdrop (None): only swap the theme resources.
        ApplicationThemeManager.Apply(
            IsLightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark,
            Wpf.Ui.Controls.WindowBackdropType.None,
            updateAccent: true);
        SaveSettings();
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
        OnPropertyChanged(nameof(ScannedCountText));
        Summary = null;
        ReportSections.Clear();
        ProgressValue = 0;
        CurrentFileName = "";
        SelectedTabIndex = 0;
        StatusText = Res.VM_SelectFolder;
        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdates = true;
        StatusText = Res.VM_CheckingUpdate;
        try
        {
            var update = await _updateService.CheckAsync();
            if (update == null)
            {
                StatusText = string.Format(Res.VM_UpToDate, AppUpdateService.CurrentVersion);
                MessageBox.Show(
                    string.Format(Res.VM_UpToDateBox, AppUpdateService.CurrentVersion),
                    Res.VM_CheckUpdateTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var notes = string.IsNullOrWhiteSpace(update.Notes)
                ? ""
                : "\n\n" + string.Format(Res.VM_UpdateNotes, update.Notes);
            var answer = MessageBox.Show(
                string.Format(Res.VM_NewVersion, update.Version, AppUpdateService.CurrentVersion, notes),
                Res.VM_NewVersionTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = Res.VM_UpdateCancelled;
                return;
            }

            StatusText = Res.VM_Downloading;
            var progress = new Progress<DownloadProgress>(p =>
            {
                var percent = p.TotalBytes is > 0 ? (int)(p.BytesReceived * 100 / p.TotalBytes.Value) : -1;
                StatusText = percent >= 0
                    ? string.Format(Res.VM_DownloadingPercent, percent)
                    : Res.VM_Downloading;
            });
            var zipPath = await _updateService.DownloadAsync(update, progress);

            // From here on there is no going back: the updater replaces the files
            // and restarts the app, so warn the user before shutting down.
            var confirm = MessageBox.Show(
                Res.VM_ReadyToUpdate,
                Res.VM_ReadyToUpdateTitle, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                StatusText = Res.VM_UpdateCancelled;
                return;
            }

            StatusText = Res.VM_ApplyingUpdate;
            _updateService.ApplyUpdateAndExit(zipPath);
        }
        catch (UpdateException ex)
        {
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Res.VM_UpdateFailed, ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }
}
