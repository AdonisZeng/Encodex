using System.IO;
using System.Text;
using Encodex.Models;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class EncodingConverterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _outputDir;

    public EncodingConverterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EncodexTests_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_tempDir, "source");
        _outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ConvertAsync_GBKToUTF8_ContentIsCorrect()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var filePath = Path.Combine(_sourceDir, "test.txt");
        var expectedText = "你好世界";
        File.WriteAllBytes(filePath, gbk.GetBytes(expectedText));

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        var content = File.ReadAllText(Path.Combine(_outputDir, "test.txt"), new UTF8Encoding(false));
        Assert.Equal(expectedText, content);
        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.Equal(1, summary.Success);
    }

    [Fact]
    public async Task ConvertAsync_UTF8ToGBK_ContentIsCorrect()
    {
        var utf8 = new UTF8Encoding(false);
        var filePath = Path.Combine(_sourceDir, "test.txt");
        var expectedText = "你好世界";
        File.WriteAllBytes(filePath, utf8.GetBytes(expectedText));

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "utf-8",
            TargetEncoding = "GBK",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var gbk = Encoding.GetEncoding("GBK");
        await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, gbk);

        var content = gbk.GetString(File.ReadAllBytes(Path.Combine(_outputDir, "test.txt")));
        Assert.Equal(expectedText, content);
        Assert.Equal(ConversionStatus.Success, item.Status);
    }

    [Fact]
    public async Task ConvertAsync_UnknownEncoding_CopiesAsIsAndCountsAsCopied()
    {
        var filePath = Path.Combine(_sourceDir, "data.bin");
        var originalBytes = new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF };
        File.WriteAllBytes(filePath, originalBytes);

        var item = new FileConversionItem
        {
            RelativePath = "data.bin",
            FileName = "data.bin",
            FileSize = originalBytes.Length,
            DetectedEncoding = null,
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        var copiedBytes = File.ReadAllBytes(Path.Combine(_outputDir, "data.bin"));
        Assert.Equal(originalBytes, copiedBytes);
        Assert.Equal(ConversionStatus.Copied, item.Status);
        Assert.Equal("检测失败，原样复制", item.StatusMessage);
        Assert.Equal(1, summary.Copied);
        Assert.Equal(0, summary.Success);
    }

    [Fact]
    public async Task ConvertAsync_UndecodableSource_CopiesOriginalInsteadOfGarbage()
    {
        var filePath = Path.Combine(_sourceDir, "broken.txt");
        // 0xC3 starts a 2-byte UTF-8 sequence but 0x28 is not a continuation byte;
        // with strict decoding this must not silently become replacement characters
        // and be reported as a successful conversion.
        var originalBytes = new byte[] { 0xC3, 0x28, 0x41, 0x42 };
        File.WriteAllBytes(filePath, originalBytes);

        var item = new FileConversionItem
        {
            RelativePath = "broken.txt",
            FileName = "broken.txt",
            FileSize = originalBytes.Length,
            DetectedEncoding = "utf-8",
            TargetEncoding = "GBK",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, Encoding.GetEncoding("GBK"));

        var outputBytes = File.ReadAllBytes(Path.Combine(_outputDir, "broken.txt"));
        Assert.Equal(originalBytes, outputBytes);
        Assert.Equal(ConversionStatus.Copied, item.Status);
        Assert.Equal("解码失败，原样复制", item.StatusMessage);
        Assert.Equal(1, summary.Copied);
    }

    [Fact]
    public async Task ConvertAsync_UnrepresentableTarget_CopiesOriginalInsteadOfGarbage()
    {
        var filePath = Path.Combine(_sourceDir, "chinese.txt");
        var utf8 = new UTF8Encoding(false);
        File.WriteAllBytes(filePath, utf8.GetBytes("你好世界"));

        var item = new FileConversionItem
        {
            RelativePath = "chinese.txt",
            FileName = "chinese.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "utf-8",
            TargetEncoding = "ascii",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, Encoding.ASCII);

        var outputBytes = File.ReadAllBytes(Path.Combine(_outputDir, "chinese.txt"));
        Assert.Equal(File.ReadAllBytes(filePath), outputBytes);
        Assert.Equal(ConversionStatus.Copied, item.Status);
        Assert.Equal("目标编码无法表示部分字符，原样复制", item.StatusMessage);
        Assert.Equal(1, summary.Copied);
    }

    [Fact]
    public async Task ConvertAsync_SameEncodingAndBom_CopiesWithoutRewriting()
    {
        var filePath = Path.Combine(_sourceDir, "plain.txt");
        File.WriteAllBytes(filePath, new UTF8Encoding(false).GetBytes("Hello"));

        var item = new FileConversionItem
        {
            RelativePath = "plain.txt",
            FileName = "plain.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "utf-8",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.Equal(ConversionStatus.Skipped, item.Status);
        Assert.Equal("编码相同，原样复制", item.StatusMessage);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Success);
        Assert.True(File.Exists(Path.Combine(_outputDir, "plain.txt")));
    }

    [Fact]
    public async Task CopyUnmatchedFilesAsync_CopiesAllFiles()
    {
        var filePath = Path.Combine(_sourceDir, "image.png");
        File.WriteAllBytes(filePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var converter = new EncodingConverter();
        var result = await converter.CopyUnmatchedFilesAsync(
            _sourceDir, _outputDir,
            new List<string> { filePath });

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, result.Failed);
        Assert.Equal("image.png", Assert.Single(result.CopiedFiles));
        Assert.True(File.Exists(Path.Combine(_outputDir, "image.png")));
    }

    [Fact]
    public async Task CopyUnmatchedFilesAsync_MissingSource_CountsFailureAndContinues()
    {
        var existing = Path.Combine(_sourceDir, "ok.png");
        File.WriteAllBytes(existing, new byte[] { 0x01 });
        var missing = Path.Combine(_sourceDir, "gone.png");

        var converter = new EncodingConverter();
        var result = await converter.CopyUnmatchedFilesAsync(
            _sourceDir, _outputDir,
            new List<string> { existing, missing });

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.Equal("gone.png", Assert.Single(result.FailedFiles));
        Assert.True(File.Exists(Path.Combine(_outputDir, "ok.png")));
    }

    [Fact]
    public async Task ConvertAsync_PreservesDirectoryStructure()
    {
        var subDir = Path.Combine(_sourceDir, "sub", "deep");
        Directory.CreateDirectory(subDir);
        var utf8 = new UTF8Encoding(false);
        var filePath = Path.Combine(subDir, "module.cs");
        File.WriteAllBytes(filePath, utf8.GetBytes("code"));

        var item = new FileConversionItem
        {
            RelativePath = Path.Combine("sub", "deep", "module.cs"),
            FileName = "module.cs",
            FileSize = 4,
            DetectedEncoding = "utf-8",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.True(File.Exists(Path.Combine(_outputDir, "sub", "deep", "module.cs")));
    }

    [Fact]
    public async Task ConvertAsync_UTF8WithBom_StripsBomBeforeReencode()
    {
        var utf8BOM = new UTF8Encoding(true);
        var filePath = Path.Combine(_sourceDir, "test.txt");
        // UTF8Encoding.GetBytes never includes the preamble; write it explicitly
        // so the source file really starts with EF BB BF.
        var bytes = utf8BOM.GetPreamble().Concat(utf8BOM.GetBytes("Hello")).ToArray();
        File.WriteAllBytes(filePath, bytes);

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "utf-8",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        // Target: UTF-8 without BOM
        await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        var outputBytes = File.ReadAllBytes(Path.Combine(_outputDir, "test.txt"));
        // Should NOT have BOM
        Assert.False(outputBytes.Length >= 3 && outputBytes[0] == 0xEF && outputBytes[1] == 0xBB && outputBytes[2] == 0xBF);
        Assert.Equal("Hello", new UTF8Encoding(false).GetString(outputBytes));
        Assert.Equal(ConversionStatus.Success, item.Status);
    }

    [Fact]
    public async Task ConvertAsync_GBKToUTF8WithBom_WritesExactlyOneBom()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var filePath = Path.Combine(_sourceDir, "test.txt");
        File.WriteAllBytes(filePath, gbk.GetBytes("你好世界"));

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(true));

        var outputBytes = File.ReadAllBytes(Path.Combine(_outputDir, "test.txt"));
        // Exactly one BOM up front, no second one right after it.
        Assert.True(outputBytes.Length >= 3 && outputBytes[0] == 0xEF && outputBytes[1] == 0xBB && outputBytes[2] == 0xBF);
        Assert.False(outputBytes.Length >= 6 && outputBytes[3] == 0xEF && outputBytes[4] == 0xBB && outputBytes[5] == 0xBF);
        Assert.Equal("你好世界", new UTF8Encoding(false).GetString(outputBytes, 3, outputBytes.Length - 3));
    }
}
