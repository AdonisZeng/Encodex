using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class CliRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public CliRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EncodexCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Run_UnknownArgument_ReturnsErrorCode()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(new[] { "--bogus" }, output);

        Assert.Equal(2, exitCode);
        Assert.Contains("未知参数", output.ToString());
    }

    [Fact]
    public void Run_Help_PrintsUsageAndReturnsZero()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(new[] { "--help" }, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("--src", output.ToString());
    }

    [Fact]
    public void Run_OverwriteAndOutConflict_ReturnsErrorCode()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(
            new[] { "--src", _tempDir, "--target", "utf-8", "--overwrite", "--out", "x" }, output);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_ConvertsFilesToDefaultOutputFolder()
    {
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);
        // Long enough for Ude to reliably detect GBK (short samples are undetectable).
        var text = "这是一段足够长的中文测试文本，用于让编码检测器能够可靠地识别出 GBK 编码。中华人民共和国成立于一九四九年。";
        File.WriteAllBytes(Path.Combine(srcDir, "a.txt"),
            Encoding.GetEncoding("GBK").GetBytes(text));

        var output = new StringWriter();
        var exitCode = CliRunner.Run(
            new[] { "--src", srcDir, "--target", "utf-8", "--ext", ".txt" }, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("转换完成", output.ToString());
        Assert.Equal(text, File.ReadAllText(
            Path.Combine(_tempDir, "src_utf-8", "a.txt"), new UTF8Encoding(false)));
    }

    // ---- B8: the CLI output folder must distinguish BOM variants like the GUI ----

    [Fact]
    public void Run_Utf8WithBom_OutputFolderDistinguishesBomVariant()
    {
        var srcDir = Path.Combine(_tempDir, "src3");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "hello");

        var output = new StringWriter();
        var exitCode = CliRunner.Run(
            new[] { "--src", srcDir, "--target", "utf-8-bom", "--ext", ".txt" }, output);

        Assert.Equal(0, exitCode);
        // WebName is "utf-8" for both BOM variants; the folder must still differ.
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "src3_utf-8-bom")));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "src3_utf-8")));
    }

    [Fact]
    public void Run_MissingSrc_ReturnsErrorCode()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(new[] { "--target", "utf-8" }, output);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_NonexistentSrc_ReturnsErrorCode()
    {
        var output = new StringWriter();
        var missing = Path.Combine(_tempDir, "no-such-dir");

        var exitCode = CliRunner.Run(new[] { "--src", missing, "--target", "utf-8" }, output);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_MissingTarget_ReturnsErrorCode()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(new[] { "--src", _tempDir }, output);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_UnsupportedTarget_ReturnsErrorCode()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(
            new[] { "--src", _tempDir, "--target", "klingon-9" }, output);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_AllInvalidExtensions_ReturnsErrorCode()
    {
        var output = new StringWriter();

        var exitCode = CliRunner.Run(
            new[] { "--src", _tempDir, "--target", "utf-8", "--ext", "c++;.." }, output);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_InvalidExtension_WarnsButKeepsTheValidOnes()
    {
        var srcDir = Path.Combine(_tempDir, "src-warn");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "hello");

        var output = new StringWriter();
        var exitCode = CliRunner.Run(
            new[] { "--src", srcDir, "--target", "utf-8", "--ext", ".txt,c++" }, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("c++", output.ToString());
        Assert.True(File.Exists(Path.Combine(_tempDir, "src-warn_utf-8", "a.txt")));
    }

    [Fact]
    public void Run_OutOption_WritesToSpecifiedDirectory()
    {
        var srcDir = Path.Combine(_tempDir, "src-out");
        var outDir = Path.Combine(_tempDir, "custom-out");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "hello");

        var output = new StringWriter();
        var exitCode = CliRunner.Run(
            new[] { "--src", srcDir, "--target", "utf-8", "--ext", ".txt", "--out", outDir }, output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outDir, "a.txt")));
        // No auto-named sibling folder may appear next to the source.
        Assert.False(Directory.Exists(srcDir + "_utf-8"));
    }

    [Fact]
    public void Run_FailedConversion_ReturnsExitCode1()
    {
        // A read-only file already sitting at the output path makes the atomic
        // replace fail, which must surface as exit code 1 (not a crash).
        var srcDir = Path.Combine(_tempDir, "src-fail");
        var outDir = Path.Combine(_tempDir, "out-fail");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(outDir);
        var text = "这是一段足够长的中文测试文本，用于让编码检测器能够可靠地识别出 GBK 编码。中华人民共和国成立于一九四九年。";
        File.WriteAllBytes(Path.Combine(srcDir, "a.txt"),
            Encoding.GetEncoding("GBK").GetBytes(text));
        var blocking = Path.Combine(outDir, "a.txt");
        File.WriteAllText(blocking, "occupied");
        File.SetAttributes(blocking, FileAttributes.ReadOnly);

        try
        {
            var output = new StringWriter();
            var exitCode = CliRunner.Run(
                new[] { "--src", srcDir, "--target", "utf-8", "--ext", ".txt", "--out", outDir }, output);

            Assert.Equal(1, exitCode);
            Assert.Contains("失败", output.ToString());
        }
        finally
        {
            File.SetAttributes(blocking, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Run_UnreadableFile_FailsCleanlyInsteadOfCrashing()
    {
        // Regression: a file that turns unreadable between the scan and the
        // detection pass used to crash the CLI with an unhandled exception.
        var srcDir = Path.Combine(_tempDir, "src-unreadable");
        Directory.CreateDirectory(srcDir);
        var locked = Path.Combine(srcDir, "secret.txt");
        File.WriteAllText(locked, "content that cannot be read back");
        DenyRead(locked);

        try
        {
            var output = new StringWriter();
            var exitCode = CliRunner.Run(
                new[] { "--src", srcDir, "--target", "utf-8", "--ext", ".txt" }, output);

            // The batch completes; the unreadable file is reported as failed
            // (it cannot even be copied), which maps to exit code 1.
            Assert.Equal(1, exitCode);
            Assert.Contains("secret.txt", output.ToString());
        }
        finally
        {
            AllowRead(locked);
        }
    }

    private static void DenyRead(string path)
    {
        var security = File.GetAccessControl(path);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().Name, FileSystemRights.Read, AccessControlType.Deny));
        File.SetAccessControl(path, security);
    }

    private static void AllowRead(string path)
    {
        var security = File.GetAccessControl(path);
        security.RemoveAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().Name, FileSystemRights.Read, AccessControlType.Deny));
        File.SetAccessControl(path, security);
    }

    [Fact]
    public void Run_OverwriteMode_RewritesSourceAndReportsBackup()
    {
        var srcDir = Path.Combine(_tempDir, "src2");
        Directory.CreateDirectory(srcDir);
        var text = "这是一段足够长的中文测试文本，用于让编码检测器能够可靠地识别出 GBK 编码。中华人民共和国成立于一九四九年。";
        File.WriteAllBytes(Path.Combine(srcDir, "a.txt"),
            Encoding.GetEncoding("GBK").GetBytes(text));

        var output = new StringWriter();
        var exitCode = CliRunner.Run(
            new[] { "--src", srcDir, "--target", "utf-8", "--ext", ".txt", "--overwrite" }, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("备份目录", output.ToString());
        Assert.Equal(text, File.ReadAllText(Path.Combine(srcDir, "a.txt"), new UTF8Encoding(false)));
    }
}
