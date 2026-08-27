using System.Diagnostics;
using System.IO.Compression;
using System.Windows;

namespace Encodex.Updater;

/// <summary>
/// Standalone updater launched by the main app right before it exits.
/// Waits for the main process to terminate (releasing its file locks),
/// overwrites the install directory from the downloaded zip, then relaunches the app.
/// Usage: Encodex.Updater.exe &lt;mainProcessId&gt; &lt;zipPath&gt; &lt;installDirectory&gt;
/// </summary>
internal static class Program
{
    private const string UpdaterBackupExtension = ".upd-old";
    private const string MainAppFileName = "Encodex.exe";

    /// <summary>Zone.Identifier ADS that Windows attaches to downloaded files.</summary>
    private const string ZoneIdentifierStream = ":Zone.Identifier";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "Encodex-Updater.log");

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Encodex.Updater.exe <mainProcessId> <zipPath> <installDirectory>");
            return 1;
        }

        Log($"Updater started: pid={args[0]}, zip={args[1]}, dir={args[2]}");

        // All path handling happens inside the try block: a malformed argument
        // (see ResolveInstallDirectory) must surface as a dialog plus log, never
        // as an unhandled exception that leaves the user with a dead app.
        string? appPath = null;
        try
        {
            var installDirectory = ResolveInstallDirectory(args[2]);
            var zipPath = ResolveZipPath(args[1]);
            appPath = Path.Combine(installDirectory, MainAppFileName);

            if (!Run(int.Parse(args[0]), zipPath, installDirectory))
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
    /// Waits for the main process, removes stale updater backups, extracts the zip
    /// and relaunches the app.
    /// </summary>
    /// <returns>false when the update was aborted because the main process did not
    /// exit within the wait window; the main app is still running in that case.</returns>
    private static bool Run(int mainProcessId, string zipPath, string installDirectory)
    {
        if (!WaitForProcessExit(mainProcessId))
        {
            ShowTimeoutWarning();
            return false;
        }

        // Leftovers from a previous updater self-replacement; safe to delete now.
        DeleteFilesWithExtension(installDirectory, UpdaterBackupExtension);

        var updaterPath = Path.Combine(installDirectory, "Encodex.Updater.exe");
        var extractedUpdater = Extract(installDirectory, zipPath, updaterPath);

        // Self-replace if the zip ships a newer updater: the running updater's own exe
        // is locked, but renaming a running exe is allowed, so move the old one aside
        // and swap the new one in.
        if (extractedUpdater != null && !string.Equals(
                extractedUpdater, updaterPath, StringComparison.OrdinalIgnoreCase))
        {
            var backup = updaterPath + UpdaterBackupExtension;
            var movedOldAside = false;
            if (File.Exists(updaterPath))
            {
                Retry(() => File.Move(updaterPath, backup), $"备份旧更新器 {updaterPath}");
                movedOldAside = true;
            }

            try
            {
                Retry(() => File.Move(extractedUpdater, updaterPath), $"替换更新器 {extractedUpdater}");
            }
            catch
            {
                // The old exe is already moved aside; restore it so the updater is not
                // permanently lost when swapping the new one in fails.
                if (movedOldAside)
                {
                    try { Retry(() => File.Move(backup, updaterPath), $"恢复旧更新器 {updaterPath}"); }
                    catch (Exception rollbackEx) { Log($"回滚更新器失败: {rollbackEx}"); }
                }
                throw;
            }

            TryDelete(extractedUpdater);
        }

        Log("Relaunching Encodex.exe");
        TryStart(appPath: Path.Combine(installDirectory, MainAppFileName));
        return true;
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

    /// <summary>
    /// Extracts every entry of the zip into <paramref name="installDirectory"/>,
    /// overwriting existing files. .NET Framework has no overwrite-capable
    /// ExtractToDirectory, so entries are extracted one by one.
    /// </summary>
    /// <returns>
    /// The destination path of the updater exe entry, or null if the zip does not
    /// contain one. If the destination is locked by this running process the entry
    /// is extracted to a temp location and returned for later self-replacement.
    /// </returns>
    private static string? Extract(string installDirectory, string zipPath, string lockedUpdaterPath)
    {
        string? updaterDestination = null;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue; // Directory entry.

                var destination = ResolveEntryPath(installDirectory, entry.FullName);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                if (string.Equals(destination, lockedUpdaterPath, StringComparison.OrdinalIgnoreCase))
                {
                    // The running updater locks its own exe: extract aside instead.
                    destination += ".new";
                    updaterDestination = destination;
                }
                else if (updaterDestination == null &&
                         Path.GetFileName(destination).Equals("Encodex.Updater.exe", StringComparison.OrdinalIgnoreCase))
                {
                    updaterDestination = destination;
                }

                Retry(() => entry.ExtractToFile(destination, overwrite: true), $"解压 {entry.FullName}");
                UnblockFile(destination);
            }
        }

        return updaterDestination;
    }

    /// <summary>
    /// Resolves a zip entry's destination inside the install directory, rejecting any
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Best effort only. */ }
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

    private static void DeleteFilesWithExtension(string directory, string extension)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory, "*" + extension))
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
