using System.IO;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class FileScannerTests : IDisposable
{
    private readonly string _tempDir;

    public FileScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EncodexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Scan_ReturnsOnlyMatchingFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Program.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "image.png"), "png");

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var results = scanner.Scan(_tempDir, profile);

        Assert.Single(results);
        Assert.Equal("Program.cs", results[0].FileName);
    }

    [Fact]
    public void Scan_RecursesIntoSubdirectories()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "Module.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "Main.cs"), "code");

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var results = scanner.Scan(_tempDir, profile);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Scan_ReturnsEmptyListForEmptyDirectory()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var results = scanner.Scan(emptyDir, profile);

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_PopulatesRelativePath()
    {
        var subDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "Program.cs"), "code");

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var results = scanner.Scan(_tempDir, profile);

        Assert.Equal(Path.Combine("src", "Program.cs"), results[0].RelativePath);
    }

    [Fact]
    public void ScanUnmatched_ReturnsNonMatchingFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Program.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "image.png"), "png");

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var results = scanner.ScanUnmatched(_tempDir, profile);

        Assert.Single(results);
        Assert.EndsWith("image.png", results[0]);
    }

    [Fact]
    public void Scan_SkipsBuildArtifactDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        File.WriteAllText(Path.Combine(_tempDir, "src", "Real.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "bin", "Generated.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "obj", "Tmp.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, ".git", "hooks.cs"), "code");

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var results = scanner.Scan(_tempDir, profile);

        Assert.Single(results);
        Assert.Equal(Path.Combine("src", "Real.cs"), results[0].RelativePath);
    }

    [Fact]
    public void ScanAll_ReturnsMatchedAndUnmatchedInOnePass()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        File.WriteAllText(Path.Combine(_tempDir, "Main.cs"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "sub", "logo.png"), "png");

        var scanner = new FileScanner();
        var profile = new ExtensionProfile();

        var (matched, unmatched) = scanner.ScanAll(_tempDir, profile);

        Assert.Single(matched);
        Assert.Equal("Main.cs", matched[0].FileName);
        Assert.Single(unmatched);
        Assert.EndsWith("logo.png", unmatched[0]);
    }
}
