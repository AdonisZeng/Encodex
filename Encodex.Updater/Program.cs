using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;

namespace Encodex.Updater;

/// <summary>
/// Standalone updater launched by the main app right before it exits.
/// Waits for the main process to terminate (releasing its file locks),
/// overwrites the install directory from the downloaded zip, then relaunches the app.
/// Usage: Encodex.Updater.exe &lt;mainProcessId&gt; &lt;zipPath&gt; &lt;installDirectory&gt; [sha256]
/// The fourth argument is optional so older Encodex versions (which pass only three)
/// keep working; when present the package is verified against it before use.
/// </summary>
internal static class Program
{
    private const string UpdaterBackupExtension = ".upd-old";
    private const string MainAppFileName = "Encodex.exe";
    private const string UpdaterFileName = "Encodex.Updater.exe";
    private const string StagingPrefix = "Encodex-upd-";

    /// <summary>Zone.Identifier ADS that Windows attaches to downloaded files.</summary>
    private const string ZoneIdentifierStream = ":Zone.Identifier";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "Encodex-Updater.log");

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Encodex.Updater.exe <mainProcessId> <zipPath> <installDirectory> [sha256]");
            return 1;
        }

        Log($"Updater started: pid={args[0]}, zip={args[1]}, dir={args[2]}, sha={(args.Length >= 4 ? "<provided>" : "<none>")}");

        // All path handling happens inside the try block: a malformed argument
        // (see ResolveInstallDirectory) must surface as a dialog plus log, never
        // as an unhandled exception that leaves the user with a dead app.
        string? appPath = null;
        try
        {
            var installDirectory = ResolveInstallDirectory(args[2]);
            var zipPath = ResolveZipPath(args[1]);
            var expectedSha256 = SanitizePathArgument(args.Length >= 4 ? args[3] : null);
            appPath = Path.Combine(installDirectory, MainAppFileName);

            if (!Run(int.Parse(args[0]), zipPath, installDirectory, expectedSha256))
            {
                // Aborted because the main app is still running: do NOT restart it
                // here, that would spawn a second instance.
                Log("Update aborted: main process did not exit in time.");
                return 2;
            }
            Log("Update finished successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex}");
            ShowError(ex);

            // Never leave the user without a running app: even after a failed
            // (possibly partial) replacement the previous files usually still work.
            if (appPath != null)
                TryStart(appPath);
            return 1;
        }
    }

    /// <summary>
    /// Waits for the main process, verifies and stages the package, swaps the staged
    /// files into place (rolling back on any failure) and relaunches the app.
    /// </summary>
    /// <returns>false when the update was aborted because the main process did not
    /// exit within the wait window; the main app is still running in that case.</returns>
    private static bool Run(int mainProcessId, string zipPath, string installDirectory, string? expectedSha256)
    {
        if (!WaitForProcessExit(mainProcessId))
        {
            ShowTimeoutWarning();
            return false;
        }

        // Leftovers from a previous updater run; safe to delete now. This also covers a
        // staging folder left behind by an updater that crashed before its finally ran.
        DeleteFilesWithExtension(installDirectory, UpdaterBackupExtension);
        CleanupStaleStagingDirectories(installDirectory);

        var stagingDirectory = CreateStagingDirectory(installDirectory);
        try
        {
            // One handle for hashing and extracting: once the stream is open, a file
            // swapped on disk cannot change what is read from it, which closes the
            // verify-then-open window a hostile local process could otherwise use.
            using var package = OpenPackageStream(zipPath);
            VerifySha256(package, expectedSha256);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);

            ExtractInto(archive, stagingDirectory);

            // Swap every staged file into place, parking each replaced original as
            // *.upd-old so a failure rolls back to a fully consistent previous state
            // instead of leaving a half-new install directory behind.
            var undo = new List<(string Installed, string? Backup)>();
            try
            {
                ApplyStagedFiles(stagingDirectory, installDirectory, undo);
            }
            catch
            {
                Rollback(undo);
                throw;
            }

            // The swap succeeded, so the per-file backups are no longer needed. Delete
            // them BEFORE relaunching: leftover *.upd-old at startup must mean a crashed
            // update (DetectFailedUpdate relies on that signal), and keeping them would
            // hold a second copy of the app in the install folder indefinitely.
            DeleteFilesWithExtension(installDirectory, UpdaterBackupExtension);

            Log("Relaunching Encodex.exe");
            TryStart(appPath: Path.Combine(installDirectory, MainAppFileName));
            return true;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    /// <summary>
    /// Creates a staging directory on the same volume as the install directory, which
    /// is what makes the later per-file swaps atomic and cheap. A sibling of the
    /// install directory is used so the staging folder is never part of the tree it
    /// replaces; a drive root has no parent, so it falls back to a subfolder inside.
    /// </summary>
    private static string CreateStagingDirectory(string installDirectory)
    {
        var root = GetStagingRoot(installDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(root, StagingPrefix + stamp);
        for (var suffix = 2; Directory.Exists(staging); suffix++)
            staging = Path.Combine(root, $"{StagingPrefix}{stamp}-{suffix}");

        Directory.CreateDirectory(staging);
        Log($"暂存目录: {staging}");
        return staging;
    }

    /// <summary>The parent directory staging folders are created in (the install
    /// directory itself when it is a drive root with no parent).</summary>
    private static string GetStagingRoot(string installDirectory)
    {
        var trimmed = installDirectory.TrimEnd('\\');
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? trimmed : parent;
    }

    /// <summary>
    /// Removes staging folders ("Encodex-upd-*") that an updater crash left in the
    /// install directory's parent. Best-effort: a folder that cannot be deleted is
    /// simply retried on the next update run. Only stale siblings are touched; the
    /// live install directory itself is never at risk.
    /// </summary>
    internal static void CleanupStaleStagingDirectories(string installDirectory)
    {
        try
        {
            var root = GetStagingRoot(installDirectory);
            foreach (var stale in Directory.GetDirectories(root, StagingPrefix + "*"))
                TryDeleteDirectory(stale);
        }
        catch
        {
            // Best effort only: a stale staging folder is cleaned up next run.
        }
    }

    /// <summary>
    /// Extracts every entry of the archive into the staging directory, applying the
    /// path-traversal guard against the staging root.
    /// </summary>
    private static void ExtractInto(ZipArchive archive, string stagingDirectory)
    {
        foreach (var entry in archive.Entries)
        {
            if (IsSkippableEntry(entry.Name))
            {
                if (!string.IsNullOrEmpty(entry.Name))
                    Log($"跳过异常压缩包条目: {entry.FullName}");
                continue;
            }

            var destination = ResolveEntryPath(stagingDirectory, entry.FullName);
            var directory = Path.GetDirectoryName(destination);
            if (directory != null)
                Directory.CreateDirectory(directory);

            Retry(() => entry.ExtractToFile(destination, overwrite: true), $"解压 {entry.FullName}");
            UnblockFile(destination);
        }
    }

    /// <summary>
    /// Directory entries (empty name) carry no payload, and "." / ".." are malformed
    /// names that would make the destination's parent directory resolve to nothing.
    /// Both are skipped instead of crashing the updater.
    /// </summary>
    internal static bool IsSkippableEntry(string? entryName) =>
        string.IsNullOrEmpty(entryName) || entryName is "." or "..";

    /// <summary>
    /// Moves every staged file into its place in the install directory. Files that
    /// already exist are swapped atomically via <see cref="File.Replace"/>, with the
    /// original parked as *.upd-old so <paramref name="undo"/> can restore it. The
    /// running updater's own executable is replaced last: a running image cannot be
    /// written to, but it can be renamed aside and a fresh one moved in.
    /// </summary>
    private static void ApplyStagedFiles(
        string stagingDirectory,
        string installDirectory,
        List<(string Installed, string? Backup)> undo)
    {
        var stagingPrefix = stagingDirectory.TrimEnd('\\') + "\\";
        var updaterPath = Path.Combine(installDirectory, UpdaterFileName);
        string? stagedUpdater = null;

        foreach (var staged in Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = staged.Substring(stagingPrefix.Length);
            if (string.Equals(Path.Combine(installDirectory, relative), updaterPath, StringComparison.OrdinalIgnoreCase))
            {
                stagedUpdater = staged;
                continue;
            }

            InstallStagedFile(staged, relative, installDirectory, undo);
        }

        if (stagedUpdater != null)
            InstallStagedFile(stagedUpdater, UpdaterFileName, installDirectory, undo);
    }

    private static void InstallStagedFile(
        string staged,
        string relative,
        string installDirectory,
        List<(string Installed, string? Backup)> undo)
    {
        var target = Path.GetFullPath(Path.Combine(installDirectory, relative));
        if (!target.StartsWith(installDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"暂存文件越出安装目录: {relative}");

        var directory = Path.GetDirectoryName(target);
        if (directory != null)
            Directory.CreateDirectory(directory);

        if (File.Exists(target))
        {
            // Atomic: the destination is never momentarily absent, and the original
            // lands in the backup name so a rollback can restore it.
            var backup = target + UpdaterBackupExtension;
            Retry(() => File.Replace(staged, target, backup), $"替换 {relative}");
            undo.Add((target, backup));
        }
        else
        {
            Retry(() => File.Move(staged, target), $"安装 {relative}");
            undo.Add((target, null));
        }
        UnblockFile(target);
    }

    /// <summary>Restores the install directory to its pre-update state by undoing every
    /// completed swap, newest first, so a partial failure never ships.</summary>
    private static void Rollback(List<(string Installed, string? Backup)> undo)
    {
        Log($"替换失败，回滚 {undo.Count} 个文件");
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            var (installed, backup) = undo[i];
            try
            {
                if (File.Exists(installed))
                    File.Delete(installed);
                if (backup != null)
                    File.Move(backup, installed);
            }
            catch (Exception ex)
            {
                Log($"回滚 {installed} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolves the install directory from the raw command-line argument. Old Encodex
    /// versions (≤ 1.0.0.x) and Windows PowerShell 5.1 quote a directory that ends with
    /// '\' without doubling the trailing backslash, so the parsed argument arrives
    /// wrapped in a stray quote ("C:\app\" → "C:\app""). Stray quotes are stripped and,
    /// when the result is not a real directory, the updater falls back to its own
    /// directory — the updater always lives next to the main app, so this is the correct
    /// install directory in every supported deployment.
    /// </summary>
    internal static string ResolveInstallDirectory(string rawArgument)
    {
        var candidate = SanitizePathArgument(rawArgument);

        if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate))
        {
            Log($"安装目录无效（{rawArgument}），改用更新器所在目录。");
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        return Path.GetFullPath(candidate);
    }

    /// <summary>Removes stray quotes left by broken command-line escaping.</summary>
    internal static string? SanitizePathArgument(string? argument) =>
        argument is null or "" ? null : argument.Trim('"');

    /// <summary>Validates the zip path, stripping stray quotes like ResolveInstallDirectory.</summary>
    private static string ResolveZipPath(string rawArgument)
    {
        var candidate = SanitizePathArgument(rawArgument);
        if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
            throw new ArgumentException($"更新包不存在或路径无效: {rawArgument}");
        return candidate!;
    }

    /// <summary>
    /// Blocks until the main process has really exited. Polls instead of relying on a
    /// single WaitForExit timeout: extracting while the app still holds file locks is
    /// the main cause of failed updates.
    /// </summary>
    /// <returns>false when the process was still running after ~60 seconds; the caller
    /// must abort the update instead of extracting against live file locks.</returns>
    private static bool WaitForProcessExit(int processId)
    {
        for (var attempt = 0; attempt < 240; attempt++) // up to ~60 seconds
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    // Give the file system a moment to release the process' file handles.
                    Thread.Sleep(500);
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // Process already exited.
            }

            Thread.Sleep(250);
        }

        return false;
    }

    /// <summary>Timeout dialog: the main app is still running, so the update must not
    /// proceed (extracting would hit live file locks) and it must not be restarted.</summary>
    private static void ShowTimeoutWarning()
    {
        try
        {
            MessageBox.Show(
                "Encodex 未能在预期时间内退出（可能有未保存的工作），本次自动更新已中止。\n\n" +
                "当前应用仍在运行，关闭应用后可重新检查更新。",
                "Encodex 更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Nothing else we can do.
        }
    }

    private static FileStream OpenPackageStream(string zipPath) =>
        new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

    /// <summary>
    /// Verifies the package against the expected SHA-256. The hash is computed over
    /// the same stream the archive is then read from, so verifying and using the
    /// package are one indivisible step — the verify-then-open window is gone.
    /// </summary>
    internal static void VerifySha256(Stream package, string? expectedSha256)
    {
        if (expectedSha256 is not { } expected || string.IsNullOrWhiteSpace(expected))
            return; // Older main app versions do not pass a hash.

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(package);
        package.Seek(0, SeekOrigin.Begin);

        var actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        // The manifest may write the hash with dash/colon separators and mixed case;
        // normalize before comparing so a purely cosmetic format difference is not
        // reported as a corrupted package.
        var normalizedExpected = expected.Trim().Replace("-", "").Replace(":", "");
        if (!string.Equals(actual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新包校验失败（SHA-256 不一致），文件可能已被篡改或损坏。");
    }

    /// <summary>
    /// Resolves a zip entry's destination inside the staging directory, rejecting any
    /// entry that would escape it (traversal via "..", absolute paths, or alternate
    /// separators). Extracted for testability of the path-traversal guard.
    /// </summary>
    internal static string ResolveEntryPath(string installDirectory, string entryFullName)
    {
        var destination = Path.GetFullPath(Path.Combine(installDirectory, entryFullName));
        if (!destination.StartsWith(installDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Zip entry escapes the install directory: {entryFullName}");
        return destination;
    }

    /// <summary>
    /// Retries a file operation on IOException/UnauthorizedAccessException.
    /// Transient locks (antivirus scans, search indexers, handle release delays)
    /// previously aborted the whole update silently.
    /// </summary>
    private static void Retry(Action operation, string description)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                    throw new IOException($"{description} 失败（已重试 5 次），文件可能被占用或没有写入权限。", ex);

                Log($"{description}: 第 {attempt} 次失败（{ex.Message}），{attempt * 500}ms 后重试");
                Thread.Sleep(attempt * 500);
            }
        }
    }

    /// <summary>
    /// Removes the Mark-of-the-Web so replaced files are not treated as
    /// "downloaded and untrusted" (SmartScreen prompts, blocked execution).
    /// </summary>
    private static void UnblockFile(string path)
    {
        try { File.Delete(path + ZoneIdentifierStream); }
        catch { /* No ADS present or not deletable: harmless. */ }
    }

    private static void TryStart(string appPath)
    {
        try
        {
            Process.Start(appPath);
        }
        catch (Exception ex)
        {
            Log($"无法启动 {appPath}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Best effort only. */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* Best effort: a stale staging folder is cleaned up next run. */ }
    }

    /// <summary>Deletes every file with the given extension below <paramref name="directory"/>
    /// (recursive, since staged files and their backups can live in subdirectories).</summary>
    internal static void DeleteFilesWithExtension(string directory, string extension)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory, "*" + extension, SearchOption.AllDirectories))
            TryDelete(file);
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the update itself.
        }
    }

    /// <summary>
    /// The updater runs without a console window, so errors must surface as a
    /// message box instead of disappearing into stderr.
    /// </summary>
    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"自动更新失败：{ex.Message}\n\n" +
                $"详细日志：{LogPath}\n" +
                "Encodex 将按当前版本重新启动。你可以从 GitHub Releases 手动下载最新版本并解压覆盖安装目录。",
                "Encodex 更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Nothing else we can do.
        }
    }
}
