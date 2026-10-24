using CommunityToolkit.Mvvm.ComponentModel;

namespace Encodex.Models;

public partial class FileConversionItem : ObservableObject
{
    public string RelativePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public string? DetectedEncoding { get; set; }
    public string TargetEncoding { get; set; } = "";

    /// <summary>True when the scan classified the file as binary. This is an input
    /// property of the file, deliberately separate from <see cref="Status"/>: every
    /// conversion round resets Status, so "is binary" must not be inferred from a
    /// previous round's Skipped marker.</summary>
    public bool IsBinary { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private ConversionStatus _status = ConversionStatus.Pending;

    [ObservableProperty]
    private string? _statusMessage;
}
