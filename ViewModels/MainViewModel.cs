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
    private readonly AppUpdateService _updateService = new();
    private readonly AppSettingsStore _settingsStore;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _updateCts;
    private List<string> _unmatchedFiles = new();

    /// <summary>Candidates for manually correcting a file's detected encoding.</summary>
    public string[] AvailableDetectedEncodings { get; } =
    {
        EncodingDetector.Utf8NoBomName, EncodingDetector.Utf8BomName,
        "utf-16LE", "utf-16BE", "utf-32LE", "utf-32BE",
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
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _isConverting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingUpdates;

    /// <summary>Set when a finished conversion could not re-detect the encodings it
    /// rewrote in place: converting again would decode with a stale encoding and
    /// corrupt the content, so the button locks until a fresh scan.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConvertCommand))]
    private bool _hasConverted;

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
    [NotifyCanExecuteChangedFor(nameof(DeleteBackupCommand))]
    private ConversionSummary? _summary;

    public ObservableCollection<EncodingOption> AvailableEncodings { get; }
    public ObservableCollection<ExtensionOption> ExtensionOptions { get; }
    public ObservableCollection<ExtensionGroup> ExtensionGroups { get; } = new();
    public ObservableCollection<ReportSection> ReportSections { get; } = new();

    /// <summary>Range-addable so a large scan fills the list with one UI notification
    /// instead of one per file.</summary>
    public RangeObservableCollection<FileConversionItem> FileItems { get; } = new();

    /// <summary>Icon shown on the theme toggle button: the theme the button switches to.</summary>
    public string ThemeIcon => IsLightTheme ? "🌙" : "☀️";

    /// <summary>Update button label: turns into a cancel action while a download runs.</summary>
    public string UpdateButtonText => IsDownloading ? Res.Ui_CancelUpdate : Res.Ui_CheckUpdate;

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
    public bool CanConvert => FileItems.Count > 0 && !IsConverting && !HasConverted;
    public bool CanCancel => IsConverting;
    public bool CanReset => !IsScanning && !IsConverting;
    // While a download is running the update button doubles as "cancel download".
    public bool CanCheckForUpdates => !IsCheckingUpdates || IsDownloading;
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

        // Re-create persisted custom extensions first so their saved check states
        // (in SelectedExtensions) can match an actual option below.
        var defaults = new HashSet<string>(ExtensionProfile.GetDefaultExtensions());
        if (settings.CustomExtensions.Count > 0)
        {
            var customGroup = new ExtensionGroup(Res.VM_CustomGroup, Array.Empty<ExtensionOption>());
            foreach (var ext in settings.CustomExtensions)
            {
                if (defaults.Contains(ext) || ExtensionOptions.Any(o => o.Extension == ext))
                    continue;
                var option = new ExtensionOption(ext);
                ExtensionOptions.Add(option);
                customGroup.Extensions.Add(option);
            }
            if (customGroup.Extensions.Count > 0)
                ExtensionGroups.Add(customGroup);
        }

        // Only apply the saved selection when it was actually persisted (an empty
        // list means "not saved yet" and must not uncheck everything).
        if (settings.SelectedExtensions.Count > 0)
        {
            foreach (var option in ExtensionOptions)
                option.IsSelected = settings.SelectedExtensions.Contains(option.Extension);
        }
    }

    /// <summary>Persists the current configuration so the next launch restores it.
    /// Public so the window can flush the settings when it closes — a user who picks
    /// a folder or toggles extensions and quits must not lose that configuration.</summary>
    public void SaveSettings()
    {
        // Anything outside the default set came from the user and must survive restarts.
        var defaults = new HashSet<string>(ExtensionProfile.GetDefaultExtensions());
        _settingsStore.Save(new AppSettings
        {
            IsLightTheme = IsLightTheme,
            SourceFolderPath = SourceFolderPath,
            SelectedEncoding = SelectedEncoding?.DisplayName,
            SelectedExtensions = ExtensionOptions.Where(o => o.IsSelected).Select(o => o.Extension).ToList(),
            CustomExtensions = ExtensionOptions.Where(o => !defaults.Contains(o.Extension))
                .Select(o => o.Extension).ToList()
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
        HasConverted = false;
        StatusText = Res.VM_Scanning;

        try
        {
            // Snapshot the profile and encoding before leaving the UI thread: the user
            // can still edit extensions or the target encoding while the scan runs.
            var profile = SnapshotProfile();
            var targetEncodingName = SelectedEncoding!.DisplayName;

            // Scanning and per-file detection are CPU/IO heavy: keep them off the UI
            // thread, and detect concurrently — each call is independent (local state
            // only) and the phase is disk-bound, so it parallelizes like the conversion.
            var (matched, unmatched, items) = await Task.Run(() =>
            {
                var (m, u) = _scanner.ScanAll(path, profile);

                var detected = new (FileScanResult File, DetectionResult Result)[m.Count];
                Parallel.For(0, m.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, EncodingConverter.MaxConcurrency)
                }, i => detected[i] = (m[i], _detector.Detect(m[i].FullPath)));

                // Detected in parallel, assembled in scan order so the list keeps the
                // order the user saw before this change.
                var list = new List<FileConversionItem>(m.Count);
                foreach (var (file, detection) in detected)
                {
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
            // One CollectionChanged notification instead of one per file.
            FileItems.AddRange(items);

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
            // Snapshot before any await: the user can change the mode while running.
            var overwriteInPlace = IsOverwriteInPlace;
            var sourceFolderName = Path.GetFileName(SourceFolderPath);
            var encodingName = EncodingConverter.GetEncodingFolderName(SelectedEncoding!.Encoding);

            if (overwriteInPlace)
            {
                // In-place mode rewrites the source files; the backup lives in %TEMP%.
                outputDir = SourceFolderPath;
            }
            else
            {
                // A drive root ("C:\", "D:") has no parent directory: GetDirectoryName
                // returns null there, so place the output inside the source folder instead.
                var parentDir = Path.GetDirectoryName(SourceFolderPath);
                if (string.IsNullOrEmpty(parentDir) || string.IsNullOrEmpty(sourceFolderName))
                {
                    var rootName = string.IsNullOrEmpty(sourceFolderName) ? "Encodex" : sourceFolderName;
                    outputDir = Path.Combine(SourceFolderPath, $"{rootName}_{encodingName}");
                }
                else
                {
                    // Handle name collision
                    outputDir = Path.Combine(parentDir, $"{sourceFolderName}_{encodingName}");
                    int suffix = 2;
                    while (Directory.Exists(outputDir))
                    {
                        outputDir = Path.Combine(parentDir, $"{sourceFolderName}_{encodingName}_{suffix}");
                        suffix++;
                    }

                    Directory.CreateDirectory(outputDir);
                }
            }

            var selectedItems = FileItems.Where(f => f.IsSelected).ToList();
            // In-place mode spends one full pass backing files up before converting,
            // so that work is mirrored in the progress bar too.
            var backupCount = overwriteInPlace ? selectedItems.Count : 0;
            ProgressMaximum = selectedItems.Count + backupCount + (overwriteInPlace ? 0 : _unmatchedFiles.Count);
            ProgressValue = 0;

            // Snapshot inputs used inside the background delegates: the user is not
            // prevented from changing the target encoding or picking a new folder
            // while a conversion runs.
            var sourcePath = SourceFolderPath;
            var encoding = SelectedEncoding!.Encoding;
            var unmatchedFiles = _unmatchedFiles;

            // Progress callbacks are posted asynchronously and can land after the
            // terminal write below. Phases report monotonically increasing values,
            // so taking the running maximum makes the result independent of the
            // order in which callbacks are delivered.
            var progress = new Progress<ConversionProgress>(p =>
            {
                ProgressValue = Math.Max(ProgressValue, p.Processed);
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
                overwriteInPlace,
                backupCount), _cts.Token);

            // Copy unmatched files; progress continues from where conversion stopped.
            // In-place mode has nothing to copy: the unmatched files already live in
            // the source folder.
            UnmatchedCopyResult copyResult = new();
            if (!overwriteInPlace)
            {
                copyResult = await Task.Run(() => _converter.CopyUnmatchedFilesAsync(
                    sourcePath,
                    outputDir,
                    unmatchedFiles,
                    progress,
                    selectedItems.Count,
                    _cts.Token), _cts.Token);
            }

            // Both phases report their own running total; a backup failure shrinks the
            // conversion list and the copy loop reports one short. Snap the bar to full
            // here so it always ends at 100% regardless of what either phase skipped.
            ProgressValue = ProgressMaximum;

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

            // In-place conversion rewrote the source files, so the scan-time
            // DetectedEncoding is stale: converting again from it would decode the
            // new bytes with the old encoding and corrupt the content.
            if (overwriteInPlace && !await RefreshDetectedEncodingsAsync(selectedItems, sourcePath))
                HasConverted = true;

            SelectedTabIndex = 3;
            StatusText = string.Format(Res.VM_ConvertDone,
                summary.Success, summary.Skipped, summary.Failed + copyResult.Failed, summary.Copied + copyResult.Copied);
        }
        catch (OperationCanceledException)
        {
            StatusText = Res.VM_ConvertCancelled;
            BuildPartialReport(outputDir);
        }
        catch (Exception ex)
        {
            // Unexpected failure (e.g. I/O error while backing up): surface it here,
            // otherwise the exception vanishes inside the async command and the UI
            // stays stuck on "正在转换" with no report at all.
            StatusText = string.Format(Res.VM_ConvertFailed, ex.Message);
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
            Description = Res.VM_FolderDialogDescription
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

        var input = dialog.ExtensionInput.Trim();
        switch (TryAddExtension(group, input))
        {
            case ExtensionAddResult.Added:
                return;
            case ExtensionAddResult.Duplicate:
                MessageBox.Show(
                    string.Format(Res.VM_DuplicateExtension, input),
                    Res.VM_AddExtensionTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            default:
                MessageBox.Show(
                    string.Format(Res.VM_InvalidExtension, input),
                    Res.VM_AddExtensionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
        }
    }

    /// <summary>
    /// Validates and appends a user-entered extension. <see cref="ExtensionOptions"/> is
    /// the single authoritative set: validation is a pure call to
    /// <see cref="ExtensionProfile.TryNormalize"/>, so nothing is mutated when the input
    /// turns out to be a duplicate or invalid.
    /// </summary>
    public ExtensionAddResult TryAddExtension(ExtensionGroup group, string input)
    {
        if (!ExtensionProfile.TryNormalize(input, out var normalized))
            return ExtensionAddResult.Invalid;

        if (ExtensionOptions.Any(o => o.Extension == normalized))
            return ExtensionAddResult.Duplicate;

        var option = new ExtensionOption(normalized);
        ExtensionOptions.Add(option);
        group.Extensions.Add(option);
        return ExtensionAddResult.Added;
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

    /// <summary>
    /// Re-detects the encoding of files that were rewritten in place. The scan-time
    /// <see cref="FileConversionItem.DetectedEncoding"/> describes the file's previous
    /// content; converting again from it would decode the new bytes with the old
    /// encoding and produce mojibake. Returns false when the refresh could not
    /// complete, in which case the caller must lock conversion until a fresh scan.
    /// </summary>
    private async Task<bool> RefreshDetectedEncodingsAsync(
        IReadOnlyList<FileConversionItem> items, string sourceDirectory)
    {
        // Only files actually rewritten in place need a refresh: Copied/Skipped/Failed
        // items still hold their original bytes.
        var rewritten = items.Where(i => i.Status == ConversionStatus.Success).ToList();
        if (rewritten.Count == 0)
            return true;

        try
        {
            // Detection is file I/O: keep it off the UI thread and detect concurrently
            // (each call is independent), matching the scan phase's parallelization.
            // Results land in per-index slots so the list keeps the input order.
            var refreshed = await Task.Run(() =>
            {
                var results = new (FileConversionItem Item, string? Encoding)[rewritten.Count];
                Parallel.For(0, rewritten.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, EncodingConverter.MaxConcurrency)
                }, i =>
                {
                    var detection = _detector.Detect(Path.Combine(sourceDirectory, rewritten[i].RelativePath));
                    results[i] = (rewritten[i], detection.EncodingName);
                });
                return results;
            });

            // Re-assigned on the UI thread so the property-change notifications keep
            // their current thread affinity.
            foreach (var (item, encoding) in refreshed)
                item.DetectedEncoding = encoding;
            return true;
        }
        catch
        {
            // A file that cannot be re-read after the rewrite must not be converted
            // again: its DetectedEncoding is stale and decoding it would corrupt it.
            StatusText = Res.VM_RefreshFailed;
            return false;
        }
    }

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

    public bool CanDeleteBackup => Summary?.BackupDirectory is { Length: > 0 };

    /// <summary>
    /// Removes the temporary in-place backup. Nothing else ever deletes it, so a few
    /// large conversions would otherwise leave gigabytes behind in %TEMP% until the
    /// user runs a disk cleanup by hand.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteBackup))]
    private void DeleteBackup()
    {
        var backup = Summary?.BackupDirectory;
        if (string.IsNullOrEmpty(backup))
            return;

        try
        {
            Directory.Delete(backup, recursive: true);

            // ConversionSummary has init-only properties, so a fresh instance replaces
            // the old one; clearing BackupDirectory hides the notice and disables the button.
            Summary = new ConversionSummary
            {
                Success = Summary!.Success,
                Skipped = Summary.Skipped,
                Failed = Summary.Failed,
                Copied = Summary.Copied,
                OutputPath = Summary.OutputPath,
                BackupDirectory = null
            };
            StatusText = string.Format(Res.VM_BackupDeleted, backup);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Res.VM_BackupDeleteFailed, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        FileItems.Clear();
        // A stale unmatched list would be copied into the next conversion run, and a
        // stale in-place flag could rewrite files the user no longer expects: every
        // piece of scan state is dropped, not just the visible list.
        _unmatchedFiles = new List<string>();
        HasConverted = false;
        IsOverwriteInPlace = false;
        SelectedFileItem = null;
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
        // While a download is running the button doubles as "cancel download".
        if (IsDownloading)
        {
            _updateCts?.Cancel();
            return;
        }

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

            if (update.Mandatory)
            {
                // A mandatory update cannot be dismissed: no Yes/No escape hatch.
                MessageBox.Show(
                    string.Format(Res.VM_MandatoryUpdate, update.Version, AppUpdateService.CurrentVersion),
                    Res.VM_NewVersionTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
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
            }

            StatusText = Res.VM_Downloading;
            var progress = new Progress<DownloadProgress>(p =>
            {
                var percent = p.TotalBytes is > 0 ? (int)(p.BytesReceived * 100 / p.TotalBytes.Value) : -1;
                StatusText = percent >= 0
                    ? string.Format(Res.VM_DownloadingPercent, percent)
                    : Res.VM_Downloading;
            });

            string zipPath;
            _updateCts = new CancellationTokenSource();
            IsDownloading = true;
            try
            {
                zipPath = await _updateService.DownloadAsync(update, progress, _updateCts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText = Res.VM_UpdateCancelled;
                return;
            }
            finally
            {
                IsDownloading = false;
                _updateCts?.Dispose();
                _updateCts = null;
            }

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
            _updateService.ApplyUpdateAndExit(zipPath, update.Sha256);
        }
        catch (UpdateException ex)
        {
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, Res.VM_CheckUpdateTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
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
