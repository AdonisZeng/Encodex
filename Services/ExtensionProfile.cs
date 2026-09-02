using System.Collections.ObjectModel;
using System.IO;

namespace Encodex.Services;

public class ExtensionProfile
{
    private readonly ObservableCollection<string> _extensions = new();

    /// <summary>Constant-time membership for <see cref="Matches"/>, which runs once per
    /// scanned file. Case-insensitive so "Program.CS" matches a stored ".cs".</summary>
    private readonly HashSet<string> _lookup = new(StringComparer.OrdinalIgnoreCase);

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
            {
                _extensions.Add(ext);
                _lookup.Add(ext);
            }
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
        if (!TryNormalize(extension, out var candidate) || !_lookup.Add(candidate))
        {
            normalized = "";
            return false;
        }

        normalized = candidate;
        _extensions.Add(candidate);
        return true;
    }

    /// <summary>
    /// Normalizes user input into a comparable extension ("TXT" → ".txt"). Pure and
    /// side-effect free, so the view model and the CLI can both validate and report
    /// precisely why an input was rejected before touching any collection.
    /// </summary>
    public static bool TryNormalize(string? extension, out string normalized)
    {
        normalized = "";
        if (extension is not { } input || string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (!trimmed.StartsWith("."))
            trimmed = "." + trimmed;

        // ".x" minimum, and everything after the dot must be a letter or digit
        // (rejects "..", ".my ext", ".c++", …).
        if (trimmed.Length < 2 || !trimmed.Skip(1).All(char.IsLetterOrDigit))
            return false;

        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    public bool RemoveExtension(string extension)
    {
        if (!TryNormalize(extension, out var normalized))
            return false;
        var removed = _extensions.Remove(normalized);
        _lookup.Remove(normalized);
        return removed;
    }

    public bool Matches(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && _lookup.Contains(ext);
    }
}
