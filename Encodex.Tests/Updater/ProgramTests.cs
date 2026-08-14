using System.IO;
using Encodex.Updater;
using Xunit;

namespace Encodex.Tests.Updater;

public class UpdaterPathTraversalTests
{
    [Fact]
    public void ResolveEntryPath_NormalEntry_ResolvesInsideInstallDirectory()
    {
        var destination = Program.ResolveEntryPath("C:\\app", "sub\\file.dll");

        Assert.Equal(Path.GetFullPath("C:\\app\\sub\\file.dll"), destination);
    }

    [Fact]
    public void ResolveEntryPath_ForwardSlashEntry_ResolvesInside()
    {
        // Zip entries use '/' separators; Path.Combine normalizes them on Windows.
        var destination = Program.ResolveEntryPath("C:\\app", "sub/file.dll");

        Assert.Equal(Path.GetFullPath("C:\\app\\sub\\file.dll"), destination);
    }

    [Fact]
    public void ResolveEntryPath_DotDotTraversal_Throws()
    {
        Assert.Throws<IOException>(() => Program.ResolveEntryPath("C:\\app", "..\\..\\evil.dll"));
    }

    [Fact]
    public void ResolveEntryPath_AbsoluteEntry_Throws()
    {
        Assert.Throws<IOException>(() => Program.ResolveEntryPath("C:\\app", "C:\\Windows\\evil.dll"));
    }

    [Fact]
    public void SanitizePathArgument_StrayTrailingQuote_Removed()
    {
        // Windows PowerShell 5.1 / old Encodex versions pass "C:\app\" unescaped,
        // which the command-line parser turns into "C:\app"" (a literal quote).
        Assert.Equal("C:\\Program Files\\Test Dir", Program.SanitizePathArgument("C:\\Program Files\\Test Dir\""));
        Assert.Equal("C:\\app", Program.SanitizePathArgument("\"C:\\app\""));
    }

    [Fact]
    public void ResolveInstallDirectory_StrayQuote_ResolvesToExistingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Encodex-Test-Dir");
        Directory.CreateDirectory(dir);
        try
        {
            // Simulates the malformed argument produced by PS 5.1 / old launchers.
            var resolved = Program.ResolveInstallDirectory(dir + '"');
            Assert.Equal(Path.GetFullPath(dir), resolved);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void ResolveInstallDirectory_InvalidDirectory_FallsBackToUpdaterDirectory()
    {
        var resolved = Program.ResolveInstallDirectory("C:\\Definitely\\Not\\A\\Real\\Dir");
        Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, resolved);
    }

    [Fact]
    public void ResolveInstallDirectory_StrayQuote_NoLongerThrowsInPathCombine()
    {
        // Regression: the unhandled "路径中具有非法字符" crash surfaced at
        // Path.Combine(installDirectory, "Encodex.exe") in Main.
        var resolved = Program.ResolveInstallDirectory("C:\\Program Files\\Test Dir\"");
        var appPath = Path.Combine(resolved, "Encodex.exe");
        Assert.EndsWith("Encodex.exe", appPath);
    }
}
