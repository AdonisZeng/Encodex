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

    public static string[] GetDefaultExtensions()
        => GetDefaultGroups().SelectMany(group => group.Extensions).ToArray();

    /// <summary>Default extensions grouped by category for display in the configuration tab.</summary>
    public static (string Name, string[] Extensions)[] GetDefaultGroups() => new[]
    {
        ("编程语言", new[]
        {
            ".cs", ".java", ".py", ".js", ".ts", ".jsx", ".tsx", ".cpp", ".c",
            ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".scala"
        }),
        ("网页前端", new[]
        {
            ".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte"
        }),
        ("数据与配置", new[]
        {
            ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".sql"
        }),
        ("文档", new[]
        {
            ".md", ".txt"
        }),
        ("脚本", new[]
        {
            ".sh", ".bat", ".ps1"
        })
    };

    public bool AddExtension(string extension)
        => AddExtension(extension, out _);

    public bool AddExtension(string extension, out string normalized)
    {
        normalized = NormalizeExtension(extension);
        if (!IsValidExtension(normalized) || _extensions.Contains(normalized))
        {
            normalized = "";
            return false;
        }
        _extensions.Add(normalized);
        return true;
    }

    private static bool IsValidExtension(string extension)
        => extension.Length >= 2 && extension[0] == '.'
            && extension.Skip(1).All(char.IsLetterOrDigit);

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
