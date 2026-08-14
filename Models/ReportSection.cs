using CommunityToolkit.Mvvm.ComponentModel;

namespace Encodex.Models;

/// <summary>One expandable section of the completion report (e.g. all files
/// that were successfully converted), with the concrete file list inside.</summary>
public partial class ReportSection : ObservableObject
{
    public string Icon { get; }
    public string Title { get; }
    public int Count { get; }

    /// <summary>Display entries: relative path, with the status reason appended when present.</summary>
    public IReadOnlyList<string> Files { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public ReportSection(string icon, string title, IReadOnlyList<string> files)
    {
        Icon = icon;
        Title = title;
        Files = files;
        Count = files.Count;
    }
}
