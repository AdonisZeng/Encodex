using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    // ---- D4: malformed zip entries must be skipped, not crash the updater ----

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void IsSkippableEntry_MalformedNames_AreSkipped(string entryName)
    {
        Assert.True(Program.IsSkippableEntry(entryName));
    }

    [Theory]
    [InlineData("Encodex.exe")]
    [InlineData("lib/Encodex.dll")]
    public void IsSkippableEntry_RealFiles_AreKept(string entryName)
    {
        Assert.False(Program.IsSkippableEntry(entryName));
    }

    // ---- A2: the updater re-verifies the package before using it ----

    [Fact]
    public void VerifySha256_MatchingHash_PassesAndRewindsStream()
    {
        var payload = "update package payload";
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);

        using var stream = new MemoryStream(bytes);
        Program.VerifySha256(stream, ComputeSha256Hex(bytes));

        // The archive is read from the same stream afterwards, so it must be rewound.
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void VerifySha256_MismatchedHash_Throws()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("tampered");
        using var stream = new MemoryStream(bytes);

        Assert.Throws<IOException>(() =>
            Program.VerifySha256(stream, new string('0', 64)));
    }

    [Fact]
    public void VerifySha256_NoExpectedHash_LegacyArgument_SkipsCheck()
    {
        // Older main app versions pass only three arguments, so no hash is available.
        var bytes = System.Text.Encoding.UTF8.GetBytes("anything");
        using var stream = new MemoryStream(bytes);

        Program.VerifySha256(stream, expectedSha256: null);
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}

public class UpdaterCleanupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "encodex-updater-tests-" + Guid.NewGuid().ToString("N"));

    public UpdaterCleanupTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ---- E1: a successful update must not leave *.upd-old backups behind ----

    [Fact]
    public void DeleteFilesWithExtension_RecursivelyDeletesMatchingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        File.WriteAllText(Path.Combine(_tempDir, "a.upd-old"), "old");
        File.WriteAllText(Path.Combine(_tempDir, "sub", "b.upd-old"), "old");
        File.WriteAllText(Path.Combine(_tempDir, "keep.txt"), "keep");

        Program.DeleteFilesWithExtension(_tempDir, ".upd-old");

        Assert.False(File.Exists(Path.Combine(_tempDir, "a.upd-old")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "sub", "b.upd-old")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "keep.txt")));
    }

    // ---- E2: a crashed updater leaves its staging folder in the install parent ----

    [Fact]
    public void CleanupStaleStagingDirectories_DeletesOnlyStagingFolders()
    {
        var installDir = Path.Combine(_tempDir, "app");
        Directory.CreateDirectory(installDir);
        var stale = Path.Combine(_tempDir, "Encodex-upd-20260101-000000");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "Encodex.exe"), "staged");
        Directory.CreateDirectory(Path.Combine(_tempDir, "Keep-This-Dir"));

        Program.CleanupStaleStagingDirectories(installDir);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(installDir));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "Keep-This-Dir")));
    }

    [Fact]
    public void CleanupStaleStagingDirectories_NoStaging_LeavesInstallDirUntouched()
    {
        var installDir = Path.Combine(_tempDir, "app");
        Directory.CreateDirectory(installDir);

        Program.CleanupStaleStagingDirectories(installDir);

        Assert.True(Directory.Exists(installDir));
        Assert.DoesNotContain(Directory.GetDirectories(_tempDir),
            d => Path.GetFileName(d).StartsWith("Encodex-upd-"));
    }

    // ---- A2 extension: the manifest hash may be uppercase with dash separators ----

    [Fact]
    public void VerifySha256_DashedUppercaseHash_Accepted()
    {
        var payload = "update package payload";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var dashed = BitConverter.ToString(SHA256.Create().ComputeHash(bytes)); // "AB-CD-..."

        using var stream = new MemoryStream(bytes);
        Program.VerifySha256(stream, dashed);

        Assert.Equal(0, stream.Position);
    }
}
