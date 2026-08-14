using CommunityToolkit.Mvvm.ComponentModel;

namespace Encodex.Models;

/// <summary>A selectable file extension shown as a checkbox in the configuration tab.</summary>
public partial class ExtensionOption : ObservableObject
{
    public string Extension { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    public ExtensionOption(string extension)
    {
        Extension = extension;
    }
}
