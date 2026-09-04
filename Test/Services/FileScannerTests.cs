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

    // ---- C6: the explicit-stack walk must survive deep nesting ----

    [Fact]
    public void Scan_HandlesDeeplyNestedDirectories()
    {
        var dir = _tempDir;
        for (int level = 0; level < 40; level++)
        {
            dir = Path.Combine(dir, $"level{level:00}");
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(Path.Combine(dir, "Deep.cs"), "code");

        var scanner = new FileScanner();
        var results = scanner.Scan(_tempDir, new ExtensionProfile());

        Assert.Single(results);
        Assert.Equal("Deep.cs", results[0].FileName);
    }

    [Fact]
    public void Scan_DoesNotSkipRootWhenRootIsNamedLikeBuildFolder()
    {
        // The skip list applies to subdirectories only: a user who deliberately
        // points the tool at a "bin" folder still gets its files.
        var root = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Inside.cs"), "code");

        var scanner = new FileScanner();
        var results = scanner.Scan(root, new ExtensionProfile());

        Assert.Single(results);
        Assert.Equal("Inside.cs", results[0].FileName);
    }

    [Fact]
    public void Scan_SkipsDirectoryNamesCaseInsensitively()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "OBJ"));
        File.WriteAllText(Path.Combine(_tempDir, "OBJ", "Tmp.cs"), "code");

        var scanner = new FileScanner();
        var results = scanner.Scan(_tempDir, new ExtensionProfile());

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_SkipsHiddenAndSystemFiles()
    {
        var hidden = Path.Combine(_tempDir, "Hidden.cs");
        var system = Path.Combine(_tempDir, "System.cs");
        File.WriteAllText(hidden, "code");
        File.WriteAllText(system, "code");
        File.WriteAllText(Path.Combine(_tempDir, "Normal.cs"), "code");
        try
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
            File.SetAttributes(system, FileAttributes.System);

            var results = new FileScanner().Scan(_tempDir, new ExtensionProfile());

            var names = results.Select(r => r.FileName).ToArray();
            Assert.Equal(new[] { "Normal.cs" }, names);
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            File.SetAttributes(system, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Scan_MatchesExtensionsCaseInsensitively()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Program.CS"), "code");

        var results = new FileScanner().Scan(_tempDir, new ExtensionProfile());

        Assert.Single(results);
        Assert.Equal("Program.CS", results[0].FileName);
    }

    [Fact]
    public void Scan_DoesNotMatchExtensionlessFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Makefile"), "code");
        File.WriteAllText(Path.Combine(_tempDir, "Real.cs"), "code");

        var results = new FileScanner().Scan(_tempDir, new ExtensionProfile());

        Assert.Single(results);
        Assert.Equal("Real.cs", results[0].FileName);
    }

    [Fact]
    public void Scan_EmptyProfile_MatchesNothing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Real.cs"), "code");

        var results = new FileScanner().Scan(_tempDir, new ExtensionProfile(loadDefaults: false));

        Assert.Empty(results);
    }

    [Fact]
    public void ScanUnmatched_SkipsBuildArtifactDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "node_modules"));
        File.WriteAllText(Path.Combine(_tempDir, "node_modules", "lib.js"), "js");
        File.WriteAllText(Path.Combine(_tempDir, "image.png"), "png");

        var results = new FileScanner().ScanUnmatched(_tempDir, new ExtensionProfile());

        Assert.Single(results);
        Assert.EndsWith("image.png", results[0]);
    }

    [Fact]
    public void Scan_PopulatesFileSize()
    {
        var path = Path.Combine(_tempDir, "Sized.cs");
        File.WriteAllBytes(path, new byte[123]);

        var results = new FileScanner().Scan(_tempDir, new ExtensionProfile());

        Assert.Equal(123, Assert.Single(results).FileSize);
    }
}
