using System.IO;

namespace Encodex.Services;

/// <summary>
/// .NET Framework has no Path.GetRelativePath; this is a minimal Windows-only
/// replacement with the same behavior for absolute paths (case-insensitive drive
/// and segment comparison, ".." segments where needed).
/// </summary>
internal static class PathHelper
{
    public static string GetRelativePath(string relativeTo, string path)
    {
        var fromParts = Path.GetFullPath(relativeTo).TrimEnd('\\').Split('\\');
        var toParts = Path.GetFullPath(path).Split('\\');

        int common = 0;
        while (common < fromParts.Length && common < toParts.Length &&
               string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        var segments = new List<string>();
        for (int i = common; i < fromParts.Length; i++)
            segments.Add("..");
        for (int i = common; i < toParts.Length; i++)
            segments.Add(toParts[i]);

        return string.Join("\\", segments);
    }
}
