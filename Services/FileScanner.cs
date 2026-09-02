using System.IO;

namespace Encodex.Services;

public class FileScanResult
{
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
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
    {
        var results = new List<FileScanResult>();
        foreach (var file in EnumerateFilesRecursive(directory))
        {
            var name = Path.GetFileName(file);
            if (profile.Matches(name) && TryGetFileSize(file, out var size))
            {
                results.Add(new FileScanResult
                {
                    FullPath = file,
                    RelativePath = PathHelper.GetRelativePath(directory, file),
                    FileName = name,
                    FileSize = size
                });
            }
        }
        return results;
    }

    public List<string> ScanUnmatched(string directory, ExtensionProfile profile)
    {
        var results = new List<string>();
        foreach (var file in EnumerateFilesRecursive(directory))
        {
            if (!profile.Matches(Path.GetFileName(file)))
                results.Add(file);
        }
        return results;
    }

    /// <summary>
    /// Single-pass variant of <see cref="Scan"/> + <see cref="ScanUnmatched"/> so the
    /// directory tree is only walked once.
    /// </summary>
    public (List<FileScanResult> Matched, List<string> Unmatched) ScanAll(string directory, ExtensionProfile profile)
    {
        var matched = new List<FileScanResult>();
        var unmatched = new List<string>();

        foreach (var file in EnumerateFilesRecursive(directory))
        {
            var name = Path.GetFileName(file);
            if (profile.Matches(name))
            {
                if (TryGetFileSize(file, out var size))
                {
                    matched.Add(new FileScanResult
                    {
                        FullPath = file,
                        RelativePath = PathHelper.GetRelativePath(directory, file),
                        FileName = name,
                        FileSize = size
                    });
                }
            }
            else
            {
                unmatched.Add(file);
            }
        }

        return (matched, unmatched);
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

    private static IEnumerable<string> EnumerateFilesRecursive(string directory)
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
                    continue;

                if (entry is FileInfo file)
                    yield return file.FullName;
                else if (entry is DirectoryInfo subDir && !SkippedDirectories.Contains(subDir.Name))
                    subDirectories.Add(subDir.FullName);
            }

            // Pushed in reverse so the first subdirectory is processed next,
            // matching the depth-first order of the previous recursive walk.
            for (int i = subDirectories.Count - 1; i >= 0; i--)
                pending.Push(subDirectories[i]);
        }
    }
}
