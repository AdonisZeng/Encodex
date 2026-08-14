using System.Diagnostics;
using System.IO.Compression;

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

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Encodex.Updater.exe <mainProcessId> <zipPath> <installDirectory>");
            return 1;
        }

        try
        {
            Run(int.Parse(args[0]), args[1], args[2]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed: {ex.Message}");
            return 1;
        }
    }

    private static void Run(int mainProcessId, string zipPath, string installDirectory)
    {
        WaitForProcessExit(mainProcessId);

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
            if (File.Exists(updaterPath))
                File.Move(updaterPath, backup);
            File.Move(extractedUpdater, updaterPath);
            File.Delete(extractedUpdater);
        }

        Process.Start(Path.Combine(installDirectory, "Encodex.exe"));
    }

    private static void WaitForProcessExit(int processId)
    {
        try
        {
            // Give the main process a moment to shut down; WaitForExit then blocks
            // until all its file locks are released.
            Process.GetProcessById(processId).WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            // Process already exited.
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

                var destination = Path.GetFullPath(Path.Combine(installDirectory, entry.FullName));
                if (!destination.StartsWith(installDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Zip entry escapes the install directory: {entry.FullName}");

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

                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        return updaterDestination;
    }

    private static void DeleteFilesWithExtension(string directory, string extension)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory, "*" + extension))
        {
            try { File.Delete(file); }
            catch { /* Best effort only. */ }
        }
    }
}
