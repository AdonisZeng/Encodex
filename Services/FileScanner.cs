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
    private static readonly string[] SkippedDirectories =
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

    // .NET Framework has no EnumerationOptions: the recursion below filters these
    // attributes itself. Reparse points are skipped to avoid infinite loops through
    // symlinks/junctions; hidden and system entries are never interesting.
    private const FileAttributes SkippedAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

    private static IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        // Enumeration is lazy: exceptions (access denied, path too long, ...) surface
        // while iterating, so materialize inside the try. Unreadable directories must
        // not abort the whole scan; PathTooLongException/IOException need the catch.
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var subDirs = new List<DirectoryInfo>();
        foreach (var entry in entries)
        {
            if ((entry.Attributes & SkippedAttributes) != 0)
                continue;
            if (entry is FileInfo file)
                yield return file.FullName;
            else if (entry is DirectoryInfo subDir)
                subDirs.Add(subDir);
        }

        foreach (var subDir in subDirs)
        {
            if (SkippedDirectories.Contains(subDir.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var file in EnumerateFilesRecursive(subDir.FullName))
                yield return file;
        }
    }
}
