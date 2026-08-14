using System.IO;
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
