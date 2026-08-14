using CommunityToolkit.Mvvm.ComponentModel;

namespace Encodex.Models;

public partial class FileConversionItem : ObservableObject
{
    public string RelativePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public string? DetectedEncoding { get; set; }
    public string TargetEncoding { get; set; } = "";

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private ConversionStatus _status = ConversionStatus.Pending;

    [ObservableProperty]
    private string? _statusMessage;
}
