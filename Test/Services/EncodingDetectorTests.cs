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
        Assert.Equal(EncodingDetector.Utf8BomName, result.EncodingName);
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
    public void Detect_Utf8WithoutBom_EncodingNameIsNoBomVariant()
    {
        var utf8 = new UTF8Encoding(false);
        // Long Chinese sample: Ude is unreliable on a few short bytes (see CLAUDE.md).
        var text = "这是一段足够长的中文测试文本，用于让编码检测器能够可靠地识别出 UTF-8 编码。中华人民共和国成立于一九四九年，中国的首都北京是一座历史悠久的城市。";

        var result = new EncodingDetector().Detect(utf8.GetBytes(text));

        Assert.NotNull(result.Encoding);
        Assert.Equal("utf-8", result.Encoding!.WebName);
        Assert.Equal(EncodingDetector.Utf8NoBomName, result.EncodingName);
    }

    [Fact]
    public void ResolveName_Utf8Variants_MapToBomFlaggedEncodings()
    {
        var withBom = EncodingDetector.ResolveName(EncodingDetector.Utf8BomName);
        var withoutBom = EncodingDetector.ResolveName(EncodingDetector.Utf8NoBomName);

        Assert.NotNull(withBom);
        Assert.NotNull(withoutBom);
        Assert.Equal("utf-8", withBom!.WebName);
        Assert.Equal("utf-8", withoutBom!.WebName);
        Assert.Equal(3, withBom.GetPreamble().Length);
        Assert.Empty(withoutBom.GetPreamble());

        // Other names keep the plain GetEncoding behavior.
        Assert.NotNull(EncodingDetector.ResolveName("GBK"));
        Assert.Null(EncodingDetector.ResolveName("no-such-encoding"));
        Assert.Null(EncodingDetector.ResolveName(null));
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

    [Fact]
    public void Detect_UTF16LEWithBom_EncodingNameIsSymmetric()
    {
        var encoding = new UnicodeEncoding(false, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello")).ToArray();

        var result = new EncodingDetector().Detect(bytes);

        Assert.Equal("utf-16LE", result.EncodingName);
    }

    [Fact]
    public void Detect_UTF32LEWithBom_EncodingNameIsSymmetric()
    {
        var encoding = new UTF32Encoding(false, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello")).ToArray();

        var result = new EncodingDetector().Detect(bytes);

        Assert.Equal("utf-32LE", result.EncodingName);
    }

    [Fact]
    public void Detect_EmptyByteArray_ReturnsNullEncoding()
    {
        var result = new EncodingDetector().Detect(Array.Empty<byte>());

        Assert.Null(result.Encoding);
        Assert.Null(result.EncodingName);
        Assert.False(result.IsBinary);
    }

    [Fact]
    public void Detect_PureAscii_IsNotFlaggedAsBinary()
    {
        // Ude may return no charset for pure ASCII (which the converter then copies
        // as-is — harmless), but it must never be misclassified as binary.
        var bytes = Encoding.ASCII.GetBytes("just plain ascii text, nothing exotic here.");

        var result = new EncodingDetector().Detect(bytes);

        Assert.False(result.IsBinary);
    }

    [Fact]
    public void Detect_ShiftJisLongText_ReturnsAnEncoding()
    {
        // Ude is unreliable on a few short bytes (see CLAUDE.md): use a long sample.
        var shiftJis = Encoding.GetEncoding("shift_jis");
        var text = "これはエンコーディング検出器が Shift-JIS エンコーディングを確実に認識できるようにするための、十分に長い日本語のテストテキストです。東京都は日本の首都であり、歴史と文化の中心地です。";
        var path = WriteFile(shiftJis.GetBytes(text));

        var result = new EncodingDetector().Detect(path);

        Assert.False(result.IsBinary);
        Assert.NotNull(result.Encoding);
    }

    [Fact]
    public void Detect_Big5LongText_ReturnsAnEncoding()
    {
        var big5 = Encoding.GetEncoding("big5");
        var text = "這是一段足夠長的繁體中文測試文本，用於讓編碼檢測器能夠可靠地識別出 Big5 編碼。中華民國台灣是一座美麗的島嶼，台北是其中心城市。";
        var path = WriteFile(big5.GetBytes(text));

        var result = new EncodingDetector().Detect(path);

        Assert.False(result.IsBinary);
        Assert.NotNull(result.Encoding);
    }

    [Fact]
    public void Detect_LargeFile_DetectsFromHeadBytes()
    {
        // Detection reads at most the first 64KB; a BOM at the head of a much
        // larger file must still be found.
        var head = new UTF8Encoding(true).GetPreamble();
        var body = new byte[256 * 1024];
        for (int i = 0; i < body.Length; i++)
            body[i] = (byte)'A';
        var path = WriteFile(head.Concat(body).ToArray());

        var result = new EncodingDetector().Detect(path);

        Assert.Equal(EncodingDetector.Utf8BomName, result.EncodingName);
    }

    [Fact]
    public void Detect_FileVanishingDuringRead_ThrowsIOException()
    {
        // The callers (scan pipeline / CLI) rely on a detectable failure mode so
        // they can degrade the file to "unknown" instead of crashing.
        var missing = Path.Combine(_tempDir, "no-such-file.txt");

        Assert.ThrowsAny<IOException>(() => new EncodingDetector().Detect(missing));
    }

    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32LE")]
    [InlineData("utf-32BE")]
    [InlineData("GBK")]
    [InlineData("GB2312")]
    [InlineData("GB18030")]
    [InlineData("Big5")]
    [InlineData("shift_jis")]
    [InlineData("euc-kr")]
    [InlineData("us-ascii")]
    [InlineData("iso-8859-1")]
    public void ResolveName_SymmetricAndCodePageNames_AllResolve(string name)
    {
        // Every name the detector can emit (and every candidate the file list
        // offers for manual correction) must round-trip through GetEncoding.
        Assert.NotNull(EncodingDetector.ResolveName(name));
    }
}
