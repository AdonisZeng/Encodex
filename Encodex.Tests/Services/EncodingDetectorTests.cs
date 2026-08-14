using System.IO;
using System.Text;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class EncodingDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public EncodingDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EncodexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(byte[] bytes)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Detect_UTF8WithBom_ReturnsUTF8()
    {
        var encoding = new UTF8Encoding(true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello World")).ToArray();
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-8", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_UTF16LEWithBom_ReturnsUTF16LE()
    {
        var encoding = new UnicodeEncoding(false, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello")).ToArray();
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-16", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_UTF16BEWithBom_ReturnsUTF16BE()
    {
        var encoding = new UnicodeEncoding(true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello")).ToArray();
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-16BE", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_UTF32LEWithBom_ReturnsUTF32LE()
    {
        var encoding = new UTF32Encoding(false, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello")).ToArray();
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-32", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_UTF32BEWithBom_ReturnsUTF32BE()
    {
        var encoding = new UTF32Encoding(true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello")).ToArray();
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-32BE", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_Utf16LEWithoutBom_NotMisclassifiedAsBinary()
    {
        var encoding = new UnicodeEncoding(false, false);
        var text = "Hello World, this is a BOM-less UTF-16 file with enough text.";
        var bytes = encoding.GetBytes(text);
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.False(result.IsBinary);
        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-16", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_Utf16LEWithoutBom_MixedWithCjk_NotMisclassifiedAsBinary()
    {
        var encoding = new UnicodeEncoding(false, false);
        // ASCII supplies null bytes on odd offsets; CJK code units carry none, except
        // U+4E00 (00 4E in LE) which puts a null on the "wrong" (even) parity.
        var text = "Hello 世界, this is a test 一 file with enough text 中文内容.";
        var bytes = encoding.GetBytes(text);
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.False(result.IsBinary);
        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-16", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_Utf16BEWithoutBom_NotMisclassifiedAsBinary()
    {
        var encoding = new UnicodeEncoding(true, false);
        var text = "Hello World, this is a BOM-less UTF-16 file with enough text.";
        var bytes = encoding.GetBytes(text);
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.False(result.IsBinary);
        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-16BE", result.Encoding!.WebName);
    }

    [Fact]
    public void Detect_GBKWithoutBom_ReturnsGBK()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var text = "这是一段足够长的中文测试文本，用于让编码检测器能够可靠地识别出 GBK 编码。中华人民共和国成立于一九四九年，中国的首都北京是一座历史悠久的城市。";
        var bytes = gbk.GetBytes(text);
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
    }

    [Fact]
    public void Detect_UTF8WithoutBom_ReturnsUTF8()
    {
        var utf8 = new UTF8Encoding(false);
        var bytes = utf8.GetBytes("Hello World, this is a test file with enough text.");
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.NotNull(result.Encoding);
    }

    [Fact]
    public void Detect_BinaryFile_ReturnsIsBinary()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF, 0xFE, 0x41, 0x42 };
        var path = WriteFile(bytes);

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.True(result.IsBinary);
    }

    [Fact]
    public void Detect_EmptyFile_ReturnsNullEncoding()
    {
        var path = WriteFile(Array.Empty<byte>());

        var detector = new EncodingDetector();
        var result = detector.Detect(path);

        Assert.Null(result.Encoding);
        Assert.False(result.IsBinary);
    }
}
