using System.Collections.ObjectModel;
using System.IO;

namespace Encodex.Services;

public class ExtensionProfile
{
    private readonly ObservableCollection<string> _extensions = new();

    public IReadOnlyList<string> Extensions => _extensions;

    public ExtensionProfile()
        : this(loadDefaults: true)
    {
    }

    /// <param name="loadDefaults">Set to false for an empty profile (used for snapshots).</param>
    internal ExtensionProfile(bool loadDefaults)
    {
        if (loadDefaults)
        {
            foreach (var ext in GetDefaultExtensions())
                _extensions.Add(ext);
        }
    }

    public static string[] GetDefaultExtensions() => new[]
    {
        ".cs", ".java", ".py", ".js", ".ts", ".jsx", ".tsx", ".md", ".txt", ".json",
        ".xml", ".html", ".htm", ".css", ".scss", ".less", ".cpp", ".c", ".h", ".hpp",
        ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".scala", ".sh", ".bat", ".ps1",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".sql", ".vue", ".svelte"
    };

    public bool AddExtension(string extension)
    {
        extension = NormalizeExtension(extension);
        if (string.IsNullOrEmpty(extension) || _extensions.Contains(extension))
            return false;
        _extensions.Add(extension);
        return true;
    }

    public bool RemoveExtension(string extension)
    {
        extension = NormalizeExtension(extension);
        return _extensions.Remove(extension);
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim();
        if (!extension.StartsWith("."))
            extension = '.' + extension;
        return extension.ToLowerInvariant();
    }

    public bool Matches(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return _extensions.Contains(ext);
    }
}
