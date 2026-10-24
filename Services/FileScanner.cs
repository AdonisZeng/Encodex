using System.IO;

namespace Encodex.Services;

public class FileScanResult
{
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
}

/// <summary>Outcome of one scan pass. Besides the files themselves it records what
/// the walk deliberately left behind: the user sees that a "node_modules" folder or a
/// hidden file was not silently ignored, and excluded folders stay distinguishable
/// from rule-based skips because they are the previous run's own output.</summary>
public class ScanAllResult
{
    /// <summary>Files whose extension matched the profile (with a readable size).</summary>
    public List<FileScanResult> Matched { get; } = new();

    /// <summary>Absolute paths of files that did not match; copied as-is on conversion.</summary>
    public List<string> Unmatched { get; } = new();

    /// <summary>Relative paths of rule-skipped folders (bin/obj/node_modules/…).</summary>
    public List<string> SkippedDirectories { get; } = new();

    /// <summary>Hidden/system/reparse-point entries (files and folders) that were skipped.</summary>
    public int HiddenEntryCount { get; set; }
}

public class FileScanner
{
    // Build artifacts and VCS folders are never interesting as conversion targets and
    // can easily dominate a scan (e.g. bin/obj inside a .NET project).
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".svn", ".hg", "node_modules", "packages",
        ".idea", ".vscode", "dist", "build", "out", "publish"
    };

    public List<FileScanResult> Scan(string directory, ExtensionProfile profile)
        => ScanAll(directory, profile).Matched;

    public List<string> ScanUnmatched(string directory, ExtensionProfile profile)
        => ScanAll(directory, profile).Unmatched;

    /// <summary>
    /// Single-pass variant of <see cref="Scan"/> + <see cref="ScanUnmatched"/> so the
    /// directory tree is only walked once.
    /// </summary>
    /// <param name="excludedDirectories">Absolute folders to skip entirely — normally
    /// the output folders of earlier runs inside the source tree. They are not reported
    /// as skipped: they are the user's own artifacts, not something a rule dropped.</param>
    public ScanAllResult ScanAll(
        string directory,
        ExtensionProfile profile,
        ICollection<string>? excludedDirectories = null)
    {
        var result = new ScanAllResult();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedDirectories != null)
        {
            foreach (var candidate in excludedDirectories)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                try
                {
                    excluded.Add(Path.GetFullPath(candidate));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // A malformed exclusion must not abort a scan that is otherwise fine.
                }
            }
        }

        Walk(directory, profile, excluded, result);
        return result;
    }

    /// <summary>Depth-first walk that fills <paramref name="result"/> in place, so the
    /// caller gets files, rule skips and hidden-entry counts from one enumeration.</summary>
    private static void Walk(
        string directory,
        ExtensionProfile profile,
        HashSet<string> excludedDirectories,
        ScanAllResult result)
    {
        // Explicit stack instead of a recursive iterator: a deeply nested tree no
        // longer builds a chain of nested state machines, so each file is yielded at
        // O(1) stack depth instead of one frame per ancestor directory.
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            // Enumeration is lazy: exceptions (access denied, path too long, ...)
            // surface while iterating, so materialize inside the try. Unreadable
            // directories must not abort the whole scan.
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(current).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var subDirectories = new List<string>();
            foreach (var entry in entries)
            {
                if ((entry.Attributes & SkippedAttributes) != 0)
                {
                    result.HiddenEntryCount++;
                    continue;
                }

                if (entry is FileInfo file)
                {
                    var name = file.Name;
                    if (profile.Matches(name))
                    {
                        if (TryGetFileSize(file.FullName, out var size))
                        {
                            result.Matched.Add(new FileScanResult
                            {
                                FullPath = file.FullName,
                                RelativePath = PathHelper.GetRelativePath(directory, file.FullName),
                                FileName = name,
                                FileSize = size
                            });
                        }
                    }
                    else
                    {
                        result.Unmatched.Add(file.FullName);
                    }
                }
                else if (entry is DirectoryInfo subDir)
                {
                    // Checked before the name rules: the previous run's output may be
                    // called "bin" and must still not be reported as a rule skip.
                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(subDir.FullName);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        continue;
                    }

                    if (excludedDirectories.Contains(fullPath))
                        continue;

                    if (SkippedDirectories.Contains(subDir.Name))
                    {
                        result.SkippedDirectories.Add(
                            PathHelper.GetRelativePath(directory, subDir.FullName));
                        continue;
                    }

                    subDirectories.Add(subDir.FullName);
                }
            }

            // Pushed in reverse so the first subdirectory is processed next,
            // matching the depth-first order of the previous recursive walk.
            for (int i = subDirectories.Count - 1; i >= 0; i--)
                pending.Push(subDirectories[i]);
        }
    }

    /// <summary>A file listed by the enumeration may vanish (or become unreadable)
    /// before we stat it; skip it rather than aborting the whole scan.</summary>
    private static bool TryGetFileSize(string file, out long size)
    {
        try
        {
            size = new FileInfo(file).Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = 0;
            return false;
        }
    }

    // .NET Framework has no EnumerationOptions: the walk below filters these
    // attributes itself. Reparse points are skipped to avoid infinite loops through
    // symlinks/junctions; hidden and system entries are never interesting.
    private const FileAttributes SkippedAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
}
