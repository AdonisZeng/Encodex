using System.Collections.ObjectModel;

namespace Encodex.Models;

/// <summary>A named category of file extensions shown as a collapsible block
/// in the configuration tab.</summary>
public class ExtensionGroup
{
    public string Name { get; }
    public ObservableCollection<ExtensionOption> Extensions { get; }

    public ExtensionGroup(string name, IEnumerable<ExtensionOption> extensions)
    {
        Name = name;
        Extensions = new ObservableCollection<ExtensionOption>(extensions);
    }
}
