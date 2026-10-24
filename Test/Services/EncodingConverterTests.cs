using System.IO;
using System.Text;
using Encodex.Models;
using Encodex.Resources;
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
        // The replace-failure fixtures leave ReadOnly attributes behind, which
        // Directory.Delete refuses to remove; clear them so no test leaks scratch.
        try
        {
            foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
        catch { }
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
    public async Task ConvertAsync_Utf8WithBomName_ToGBK_BomNotLeaked()
    {
        var utf8 = new UTF8Encoding(true);
        var filePath = Path.Combine(_sourceDir, "test.txt");
        var expectedText = "你好世界";
        File.WriteAllBytes(filePath, utf8.GetPreamble().Concat(utf8.GetBytes(expectedText)).ToArray());

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = EncodingDetector.Utf8BomName,
            TargetEncoding = "GBK",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, Encoding.GetEncoding("GBK"));

        // U+FEFF is not representable in GBK: a leaked BOM would fail the strict
        // encode and fall back to "Copied" instead of a successful conversion.
        var content = Encoding.GetEncoding("GBK").GetString(File.ReadAllBytes(Path.Combine(_outputDir, "test.txt")));
        Assert.Equal(expectedText, content);
        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.Equal(1, summary.Success);
    }

    [Fact]
    public async Task ConvertAsync_Utf8NoBomName_ToUTF8WithBom_AddsBom()
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
            DetectedEncoding = EncodingDetector.Utf8NoBomName,
            TargetEncoding = "UTF-8 (带 BOM)",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(true));

        var bytes = File.ReadAllBytes(Path.Combine(_outputDir, "test.txt"));
        var preamble = new UTF8Encoding(true).GetPreamble();
        Assert.Equal(preamble, bytes.Take(preamble.Length).ToArray());
        var content = new UTF8Encoding(false).GetString(bytes.Skip(preamble.Length).ToArray());
        Assert.Equal(expectedText, content);
        Assert.Equal(ConversionStatus.Success, item.Status);
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

    // ---- C4: concurrent copying must not scramble the report order ----

    [Fact]
    public async Task CopyUnmatchedFilesAsync_PreservesInputOrder()
    {
        var files = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            var name = $"img{i:00}.png";
            File.WriteAllBytes(Path.Combine(_sourceDir, name), new byte[] { (byte)i, 0x50 });
            files.Add(Path.Combine(_sourceDir, name));
        }

        var result = await new EncodingConverter().CopyUnmatchedFilesAsync(_sourceDir, _outputDir, files);

        Assert.Equal(24, result.Copied);
        Assert.Equal(0, result.Failed);
        Assert.Equal(files.Select(Path.GetFileName), result.CopiedFiles);
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

    [Fact]
    public async Task ConvertAsync_DetectedUtf16LE_ConvertsCorrectly()
    {
        // DetectedEncoding now uses the symmetric name "utf-16LE"; make sure the
        // converter still round-trips it correctly (no BOM corruption, no copy-as-is).
        var utf16 = new UnicodeEncoding(false, true);
        var bytes = utf16.GetPreamble().Concat(utf16.GetBytes("你好世界")).ToArray();
        var filePath = Path.Combine(_sourceDir, "test.txt");
        File.WriteAllBytes(filePath, bytes);

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = bytes.Length,
            DetectedEncoding = "utf-16LE",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var converter = new EncodingConverter();
        var summary = await converter.ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        var content = File.ReadAllText(Path.Combine(_outputDir, "test.txt"), new UTF8Encoding(false));
        Assert.Equal("你好世界", content);
        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.Equal(1, summary.Success);
    }

    [Fact]
    public async Task ConvertAsync_MultipleFiles_ParallelResultsAreCorrect()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var items = new List<FileConversionItem>();
        for (int i = 0; i < 10; i++)
        {
            var name = $"f{i}.txt";
            File.WriteAllBytes(Path.Combine(_sourceDir, name), gbk.GetBytes($"文件{i}"));
            items.Add(new FileConversionItem
            {
                RelativePath = name,
                FileName = name,
                FileSize = new FileInfo(Path.Combine(_sourceDir, name)).Length,
                DetectedEncoding = "GBK",
                TargetEncoding = "utf-8",
                IsSelected = true
            });
        }

        var summary = await new EncodingConverter().ConvertAsync(
            items, _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.Equal(10, summary.Success);
        Assert.Equal(0, summary.Failed);
        for (int i = 0; i < 10; i++)
            Assert.Equal($"文件{i}", File.ReadAllText(
                Path.Combine(_outputDir, $"f{i}.txt"), new UTF8Encoding(false)));
    }

    [Fact]
    public async Task ConvertAsync_OverwriteInPlace_BacksUpAndRewritesSource()
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

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _sourceDir, new UTF8Encoding(false), overwriteInPlace: true);

        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.NotNull(summary.BackupDirectory);
        Assert.True(Directory.Exists(summary.BackupDirectory));
        Assert.True(File.Exists(Path.Combine(summary.BackupDirectory, "test.txt")));
        // The source file itself was rewritten to UTF-8.
        Assert.Equal(expectedText, File.ReadAllText(filePath, new UTF8Encoding(false)));
        Assert.Equal(1, summary.Success);
    }

    [Fact]
    public async Task ConvertAsync_PreservesSourceTimestamps()
    {
        var filePath = Path.Combine(_sourceDir, "test.txt");
        File.WriteAllText(filePath, "hello");
        var lastWriteUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, lastWriteUtc);

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = 5,
            DetectedEncoding = "utf-8",
            TargetEncoding = "GBK",
            IsSelected = true
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, Encoding.GetEncoding("GBK"));

        Assert.Equal(ConversionStatus.Success, item.Status);
        var output = Path.Combine(_outputDir, "test.txt");
        Assert.True(File.Exists(output));
        Assert.Equal(lastWriteUtc, File.GetLastWriteTimeUtc(output));
    }

    // ---- B5: an in-place rewrite must not reset the file's timestamps ----

    [Fact]
    public async Task ConvertAsync_OverwriteInPlace_PreservesSourceTimestamps()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var filePath = Path.Combine(_sourceDir, "test.txt");
        File.WriteAllBytes(filePath, gbk.GetBytes("你好世界"));
        var lastWriteUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(filePath, lastWriteUtc);

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = 12,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _sourceDir, new UTF8Encoding(false), overwriteInPlace: true);

        Assert.Equal(ConversionStatus.Success, item.Status);
        // The source is gone by the time the rewrite finishes, so the timestamps
        // must be captured before the rewrite, not read from the rewritten file.
        Assert.Equal(lastWriteUtc, File.GetLastWriteTimeUtc(filePath));
    }

    // ---- B7: a binary file stays "skipped"; an untouched file is not "copied" ----

    [Fact]
    public async Task ConvertAsync_BinaryItemKeepsSkippedStatus()
    {
        var filePath = Path.Combine(_sourceDir, "data.bin");
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF };
        File.WriteAllBytes(filePath, bytes);

        var item = new FileConversionItem
        {
            RelativePath = "data.bin",
            FileName = "data.bin",
            FileSize = bytes.Length,
            DetectedEncoding = null,          // binary: no encoding detected
            TargetEncoding = "utf-8",
            IsSelected = true,
            IsBinary = true,
            Status = ConversionStatus.Skipped,
            StatusMessage = "二进制文件"
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.Equal(ConversionStatus.Skipped, item.Status);
        Assert.Equal(Res.VM_Binary, item.StatusMessage);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Copied);
        // Binary files are copied as-is so the separate output tree stays complete;
        // they still count as skipped because no encoding rewrite happened.
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_outputDir, "data.bin")));
    }

    // ---- B8: in-place backups must not copy files that can never be rewritten ----

    [Fact]
    public async Task ConvertAsync_OverwriteInPlace_SkippedItemNotBackedUp_ButStillCounted()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var textPath = Path.Combine(_sourceDir, "a.txt");
        File.WriteAllBytes(textPath, gbk.GetBytes("你好世界"));

        var binaryPath = Path.Combine(_sourceDir, "data.bin");
        var binary = new byte[] { 0x00, 0x01, 0x02, 0xFF };
        File.WriteAllBytes(binaryPath, binary);

        var items = new List<FileConversionItem>
        {
            new()
            {
                RelativePath = "a.txt",
                FileName = "a.txt",
                FileSize = new FileInfo(textPath).Length,
                DetectedEncoding = "GBK",
                TargetEncoding = "utf-8",
                IsSelected = true,
                Status = ConversionStatus.Pending
            },
            new()
            {
                RelativePath = "data.bin",
                FileName = "data.bin",
                FileSize = binary.Length,
                DetectedEncoding = null,   // binary: nothing to convert, never rewritten
                TargetEncoding = "utf-8",
                IsSelected = true,
                IsBinary = true,
                Status = ConversionStatus.Skipped
            }
        };

        var progress = new CollectingProgress();
        var summary = await new EncodingConverter().ConvertAsync(
            items, _sourceDir, _sourceDir, new UTF8Encoding(false),
            progress, overwriteInPlace: true);

        Assert.Equal(1, summary.Success);
        Assert.Equal(1, summary.Skipped);

        // Only the file actually rewritten in place needs a backup; the Skipped item's
        // original bytes are untouched, so copying them would be wasted IO.
        var backedUp = Directory.GetFiles(summary.BackupDirectory!, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "a.txt" }, backedUp);

        // The Skipped item still advances progress so the bar deterministically reaches Total.
        Assert.Equal(2, progress.Updates.Max(u => u.Processed));
        Assert.Equal(2, progress.Updates.Max(u => u.Total));
    }

    // ---- 每轮状态独立：上一轮的结果不得决定本轮处理谁 ----

    [Fact]
    public async Task ConvertAsync_PreviousFailure_IsResetAndConvertedAgain()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var filePath = Path.Combine(_sourceDir, "retry.txt");
        File.WriteAllBytes(filePath, gbk.GetBytes("你好世界"));

        // A previous round failed on this file; this round must neither skip it nor
        // surface the stale message. Only this round's outcome may be reported.
        var item = new FileConversionItem
        {
            RelativePath = "retry.txt",
            FileName = "retry.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true,
            Status = ConversionStatus.Failed,
            StatusMessage = "old"
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.Null(item.StatusMessage);
        Assert.Equal(1, summary.Success);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task ConvertAsync_BinaryItem_IsSkippedOnEveryRound()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF };
        var filePath = Path.Combine(_sourceDir, "data.bin");
        File.WriteAllBytes(filePath, bytes);

        // Binary-ness now lives in IsBinary, so a repeated round still classifies it
        // as skipped after the per-round status reset instead of "copying" it.
        var item = new FileConversionItem
        {
            RelativePath = "data.bin",
            FileName = "data.bin",
            FileSize = bytes.Length,
            DetectedEncoding = null,
            TargetEncoding = "utf-8",
            IsSelected = true,
            IsBinary = true,
            Status = ConversionStatus.Skipped,
            StatusMessage = Res.VM_Binary
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _sourceDir, new UTF8Encoding(false), overwriteInPlace: true);

        Assert.Equal(ConversionStatus.Skipped, item.Status);
        Assert.Equal(Res.VM_Binary, item.StatusMessage);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Copied);
        Assert.Equal(0, summary.Success);
        // Skipped binary files are never rewritten in place.
        Assert.Equal(bytes, File.ReadAllBytes(filePath));
    }

    // ---- 取消必须在大文件内部生效，而不是只在文件之间 ----

    [Fact]
    public async Task ConvertFileAsync_CancelledDuringRead_ThrowsAndLeavesNoScratchFile()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var filePath = Path.Combine(_sourceDir, "big.txt");
        File.WriteAllBytes(filePath,
            gbk.GetBytes(string.Concat(Enumerable.Repeat("取消测试文本", 1000))));

        var tempPath = Path.Combine(_sourceDir, "big.txt.1a2b3c4d.tmp");
        using var cts = new CancellationTokenSource();
        try
        {
            using var source = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var cancelling = new CancelOnFirstReadStream(source, cts);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EncodingConverter.ConvertFileAsync(
                    cancelling, tempPath, gbk, new UTF8Encoding(false),
                    Array.Empty<byte>(), cts.Token));
        }
        finally
        {
            // The scratch file belongs to this test now: ConvertFileAsync is called
            // directly, so ConvertOneAsync's cleanup finally does not run.
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task CopyUnmatchedFilesAsync_CancelledToken_ThrowsWithoutOutput()
    {
        var filePath = Path.Combine(_sourceDir, "img.png");
        File.WriteAllBytes(filePath, new byte[] { 0x89, 0x50 });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EncodingConverter().CopyUnmatchedFilesAsync(
                _sourceDir, _outputDir, new List<string> { filePath },
                cancellationToken: cts.Token));

        Assert.False(File.Exists(Path.Combine(_outputDir, "img.png")));
    }

    // ---- 回归：转换的临时文件不得占用（覆盖并销毁）项目中真实存在的同名文件 ----

    [Fact]
    public async Task ConvertAsync_OverwriteInPlace_KeepsExistingSiblingTmpFileIntact()
    {
        var gbk = Encoding.GetEncoding("GBK");
        var text = Path.Combine(_sourceDir, "a.txt");
        File.WriteAllBytes(text, gbk.GetBytes("你好世界"));

        // A real file that happens to sit exactly where the converter used to park
        // its scratch copy. It is not part of the conversion; it must survive it.
        var neighbour = Path.Combine(_sourceDir, "a.txt.tmp");
        var neighbourBytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(neighbour, neighbourBytes);

        var item = new FileConversionItem
        {
            RelativePath = "a.txt",
            FileName = "a.txt",
            FileSize = new FileInfo(text).Length,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _sourceDir, new UTF8Encoding(false), overwriteInPlace: true);

        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.True(File.Exists(neighbour), "转换过程覆盖了同目录下的真实 a.txt.tmp 文件");
        Assert.Equal(neighbourBytes, File.ReadAllBytes(neighbour));
    }

    // ---- TryReplaceFile / TryRestoreBackup：替换失败绝不允许丢数据 ----

    [Fact]
    public void TryReplaceFile_Success_ReplacesContentAndLeavesNoScratchLeftovers()
    {
        var target = Path.Combine(_sourceDir, "run.bat");
        File.WriteAllBytes(target, Encoding.ASCII.GetBytes("old"));
        // ReplaceFile 保留被替换文件的属性，替换后不得丢失。
        File.SetAttributes(target, FileAttributes.Hidden | FileAttributes.Archive);

        var temp = target + ".1a2b3c4d.tmp";
        File.WriteAllBytes(temp, Encoding.ASCII.GetBytes("new"));

        var ok = EncodingConverter.TryReplaceFile(temp, target, out var error);

        Assert.True(ok, error?.Message);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.Hidden),
            "替换后目标的属性必须保留");
        Assert.Empty(Directory.GetFiles(_sourceDir, "*" + EncodingConverter.BackupExtension));
    }

    [Fact]
    public void TryReplaceFile_MissingDestination_MovesIntoPlace()
    {
        var target = Path.Combine(_sourceDir, "new.txt");
        var temp = target + ".1a2b3c4d.tmp";
        File.WriteAllBytes(temp, Encoding.ASCII.GetBytes("converted"));

        var ok = EncodingConverter.TryReplaceFile(temp, target, out var error);

        Assert.True(ok, error?.Message);
        Assert.Equal("converted", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void TryReplaceFile_Failure_KeepsOriginalBytesAndDropsBackup()
    {
        var target = Path.Combine(_sourceDir, "run.bat");
        var original = Encoding.ASCII.GetBytes("original");
        File.WriteAllBytes(target, original);
        File.SetAttributes(target, FileAttributes.ReadOnly);   // File.Replace 必定失败

        var temp = target + ".1a2b3c4d.tmp";
        File.WriteAllBytes(temp, Encoding.ASCII.GetBytes("converted"));

        var ok = EncodingConverter.TryReplaceFile(temp, target, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.True(File.Exists(target), "替换失败后目标文件不能消失");
        Assert.Equal(original, File.ReadAllBytes(target));
        // 备份在目标完好时是冗余的，必须被清理而不是留在项目里。
        Assert.Empty(Directory.GetFiles(_sourceDir, "*" + EncodingConverter.BackupExtension));
    }

    [Fact]
    public void TryRestoreBackup_MissingDestination_RestoresOriginalBytes()
    {
        // ReplaceFile 的 ERROR_UNABLE_TO_MOVE_REPLACEMENT(1176) 在备份名为 null 时
        // 留下的正是这个状态：目标已消失、只剩备份与临时文件。恢复是唯一的自救路径。
        var target = Path.Combine(_sourceDir, "run.bat");
        var original = Encoding.ASCII.GetBytes("original-bytes");
        var backup = target + EncodingConverter.BackupExtension;
        File.WriteAllBytes(backup, original);

        var restored = EncodingConverter.TryRestoreBackup(target, backup);

        Assert.True(restored);
        Assert.True(File.Exists(target));
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void TryRestoreBackup_ExistingDestination_KeepsDestinationAndDropsBackup()
    {
        var target = Path.Combine(_sourceDir, "run.bat");
        File.WriteAllBytes(target, Encoding.ASCII.GetBytes("current"));
        var backup = target + EncodingConverter.BackupExtension;
        File.WriteAllBytes(backup, Encoding.ASCII.GetBytes("stale-original"));

        var restored = EncodingConverter.TryRestoreBackup(target, backup);

        Assert.False(restored);   // 目标完好，无需恢复
        Assert.Equal("current", File.ReadAllText(target));   // 不得被过时备份覆盖
        Assert.False(File.Exists(backup));
    }

    // ---- 边界与失败路径 ----

    [Fact]
    public async Task ConvertAsync_EmptyList_ReturnsZeroSummary()
    {
        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem>(),
            _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.Equal(0, summary.Success);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Copied);
    }

    [Fact]
    public async Task ConvertAsync_UnselectedItems_AreIgnored()
    {
        var filePath = Path.Combine(_sourceDir, "a.txt");
        File.WriteAllText(filePath, "hello");

        var item = new FileConversionItem
        {
            RelativePath = "a.txt",
            FileName = "a.txt",
            FileSize = 5,
            DetectedEncoding = "utf-8",
            TargetEncoding = "GBK",
            IsSelected = false
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, Encoding.GetEncoding("GBK"));

        Assert.Equal(0, summary.Success);
        Assert.False(File.Exists(Path.Combine(_outputDir, "a.txt")));
        // An ignored item keeps its Pending status; it was never touched.
        Assert.Equal(ConversionStatus.Pending, item.Status);
    }

    [Fact]
    public async Task ConvertAsync_AlreadyCancelledToken_ThrowsWithoutWriting()
    {
        var filePath = Path.Combine(_sourceDir, "a.txt");
        File.WriteAllText(filePath, "hello");

        var item = new FileConversionItem
        {
            RelativePath = "a.txt",
            FileName = "a.txt",
            FileSize = 5,
            DetectedEncoding = "utf-8",
            TargetEncoding = "GBK",
            IsSelected = true
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EncodingConverter().ConvertAsync(
                new List<FileConversionItem> { item },
                _sourceDir, _outputDir, Encoding.GetEncoding("GBK"),
                cancellationToken: cts.Token));

        Assert.False(File.Exists(Path.Combine(_outputDir, "a.txt")));
    }

    [Fact]
    public async Task ConvertAsync_MissingSourceFile_CountsAsFailed()
    {
        var item = new FileConversionItem
        {
            RelativePath = "gone.txt",
            FileName = "gone.txt",
            FileSize = 5,
            DetectedEncoding = "utf-8",
            TargetEncoding = "GBK",
            IsSelected = true
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, Encoding.GetEncoding("GBK"));

        Assert.Equal(1, summary.Failed);
        Assert.Equal(ConversionStatus.Failed, item.Status);
        Assert.False(string.IsNullOrEmpty(item.StatusMessage));
    }

    [Fact]
    public async Task ConvertAsync_BackupFailure_CountsAsFailedInSummary()
    {
        // A locked source makes the in-place backup fail. The item is excluded from
        // the rewrite (its only recovery copy is missing), and the summary must count
        // it as failed: the CLI derives its exit code from summary.Failed, so a silent
        // zero here would report success after a real failure.
        var filePath = Path.Combine(_sourceDir, "locked.txt");
        File.WriteAllText(filePath, "hello");
        var item = new FileConversionItem
        {
            RelativePath = "locked.txt",
            FileName = "locked.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "utf-8",
            TargetEncoding = "UTF-8 (无 BOM)",
            IsSelected = true
        };

        ConversionSummary summary;
        // The lock must be released before the content assertion: reading a file held
        // with FileShare.None would throw instead of proving it was left untouched.
        using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            summary = await new EncodingConverter().ConvertAsync(
                new List<FileConversionItem> { item },
                _sourceDir, _sourceDir, new UTF8Encoding(false),
                overwriteInPlace: true);
        }

        try
        {
            Assert.Equal(1, summary.Failed);
            Assert.Equal(0, summary.Success);
            Assert.Equal(ConversionStatus.Failed, item.Status);
            Assert.Equal(Res.Conv_BackupFailed, item.StatusMessage);
            Assert.Equal("hello", File.ReadAllText(filePath));
        }
        finally
        {
            // The backup folder lives in %TEMP% and is not owned by the fixture.
            try
            {
                if (summary.BackupDirectory != null && Directory.Exists(summary.BackupDirectory))
                    Directory.Delete(summary.BackupDirectory, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task ConvertAsync_Utf16LeBom_ToUtf16LeNoBom_StripsPreamble()
    {
        // Same code page, different BOM intent: a real conversion must run, and the
        // U+FEFF the BOM decodes to must not leak into the output text.
        var utf16Bom = new UnicodeEncoding(false, true);
        var filePath = Path.Combine(_sourceDir, "test.txt");
        File.WriteAllBytes(filePath,
            utf16Bom.GetPreamble().Concat(utf16Bom.GetBytes("你好")).ToArray());

        var item = new FileConversionItem
        {
            RelativePath = "test.txt",
            FileName = "test.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "utf-16LE",
            TargetEncoding = "UTF-16 LE",
            IsSelected = true
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UnicodeEncoding(false, false));

        var outputBytes = File.ReadAllBytes(Path.Combine(_outputDir, "test.txt"));
        Assert.False(outputBytes.Length >= 2 && outputBytes[0] == 0xFF && outputBytes[1] == 0xFE,
            "无 BOM 目标的输出不得以 FF FE 开头");
        Assert.Equal("你好", new UnicodeEncoding(false, false).GetString(outputBytes));
        Assert.Equal(ConversionStatus.Success, item.Status);
        Assert.Equal(1, summary.Success);
    }

    [Fact]
    public async Task ConvertAsync_LargeFile_StreamsCorrectly()
    {
        // Bigger than the 64KB conversion buffer: the streaming decode/re-encode
        // loop must stitch the chunks without loss or duplication.
        var gbk = Encoding.GetEncoding("GBK");
        var chunk = "编码转换流式处理测试文本，重复多次以超过内部缓冲区大小。";
        var expectedText = string.Concat(Enumerable.Repeat(chunk, 6000));
        var filePath = Path.Combine(_sourceDir, "big.txt");
        File.WriteAllBytes(filePath, gbk.GetBytes(expectedText));
        Assert.True(new FileInfo(filePath).Length > 64 * 1024);

        var item = new FileConversionItem
        {
            RelativePath = "big.txt",
            FileName = "big.txt",
            FileSize = new FileInfo(filePath).Length,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        var summary = await new EncodingConverter().ConvertAsync(
            new List<FileConversionItem> { item },
            _sourceDir, _outputDir, new UTF8Encoding(false));

        Assert.Equal(1, summary.Success);
        Assert.Equal(expectedText,
            File.ReadAllText(Path.Combine(_outputDir, "big.txt"), new UTF8Encoding(false)));
    }

    [Fact]
    public async Task ConvertAsync_OverwriteInPlace_ReadOnlyTarget_FailsAndKeepsOriginal()
    {
        // File.Replace refuses a read-only destination: the conversion must fail
        // cleanly, the original bytes must survive, and the pre-rewrite backup
        // must still exist as the recovery path.
        var gbk = Encoding.GetEncoding("GBK");
        var original = gbk.GetBytes("你好世界");
        var filePath = Path.Combine(_sourceDir, "locked.txt");
        File.WriteAllBytes(filePath, original);
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        var item = new FileConversionItem
        {
            RelativePath = "locked.txt",
            FileName = "locked.txt",
            FileSize = original.Length,
            DetectedEncoding = "GBK",
            TargetEncoding = "utf-8",
            IsSelected = true
        };

        ConversionSummary? summary = null;
        try
        {
            summary = await new EncodingConverter().ConvertAsync(
                new List<FileConversionItem> { item },
                _sourceDir, _sourceDir, new UTF8Encoding(false), overwriteInPlace: true);

            Assert.Equal(1, summary.Failed);
            Assert.Equal(ConversionStatus.Failed, item.Status);
            Assert.Equal(original, File.ReadAllBytes(filePath));
            Assert.NotNull(summary.BackupDirectory);
            Assert.True(File.Exists(Path.Combine(summary.BackupDirectory!, "locked.txt")),
                "失败的原地转换必须保留备份作为恢复路径");
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
            if (summary?.BackupDirectory != null)
            {
                try { Directory.Delete(summary.BackupDirectory, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ConvertAsync_ProgressReportsEveryFileExactlyOnce()
    {
        var items = new List<FileConversionItem>();
        for (int i = 0; i < 5; i++)
        {
            var name = $"f{i}.txt";
            File.WriteAllText(Path.Combine(_sourceDir, name), "hello");
            items.Add(new FileConversionItem
            {
                RelativePath = name,
                FileName = name,
                FileSize = 5,
                DetectedEncoding = "utf-8",
                TargetEncoding = "GBK",
                IsSelected = true
            });
        }

        var progress = new CollectingProgress();
        await new EncodingConverter().ConvertAsync(
            items, _sourceDir, _outputDir, Encoding.GetEncoding("GBK"), progress);

        // Concurrent completion makes the emission order nondeterministic, but the
        // set of reported Processed values must be exactly 1..N with a stable Total.
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5 },
            progress.Updates.Select(u => u.Processed).OrderBy(v => v).ToArray());
        Assert.All(progress.Updates, u => Assert.Equal(5, u.Total));
    }

    [Fact]
    public async Task CopyUnmatchedFilesAsync_EmptyList_ReturnsEmptyWithoutProgress()
    {
        var progress = new CollectingProgress();

        var result = await new EncodingConverter().CopyUnmatchedFilesAsync(
            _sourceDir, _outputDir, new List<string>(), progress);

        Assert.Equal(0, result.Copied);
        Assert.Equal(0, result.Failed);
        Assert.Empty(progress.Updates);
    }

    [Fact]
    public async Task CopyUnmatchedFilesAsync_PreservesNestedDirectoryStructure()
    {
        var nested = Path.Combine(_sourceDir, "sub", "deep");
        Directory.CreateDirectory(nested);
        var filePath = Path.Combine(nested, "img.png");
        File.WriteAllBytes(filePath, new byte[] { 0x89, 0x50 });

        var result = await new EncodingConverter().CopyUnmatchedFilesAsync(
            _sourceDir, _outputDir, new List<string> { filePath });

        Assert.Equal(1, result.Copied);
        Assert.Equal(Path.Combine("sub", "deep", "img.png"), Assert.Single(result.CopiedFiles));
        Assert.True(File.Exists(Path.Combine(_outputDir, "sub", "deep", "img.png")));
    }

    [Fact]
    public void GetEncodingFolderName_DistinguishesBomVariants()
    {
        Assert.Equal("utf-8", EncodingConverter.GetEncodingFolderName(new UTF8Encoding(false)));
        Assert.Equal("utf-8-bom", EncodingConverter.GetEncodingFolderName(new UTF8Encoding(true)));
        Assert.Equal("utf-16", EncodingConverter.GetEncodingFolderName(new UnicodeEncoding(false, false)));
        // Code page 936's canonical WebName is "gb2312" even when requested as "GBK".
        Assert.Equal("gb2312", EncodingConverter.GetEncodingFolderName(Encoding.GetEncoding("GBK")));
    }

    /// <summary>Synchronous IProgress collector: Progress&lt;T&gt; would marshal
    /// asynchronously without a SynchronizationContext and race the assertions.
    /// Reports arrive from concurrent worker threads, so the backing list is guarded
    /// by a lock — a bare List&lt;T&gt;.Add corrupts under parallel writers.</summary>
    private sealed class CollectingProgress : IProgress<ConversionProgress>
    {
        private readonly object _gate = new();
        private readonly List<ConversionProgress> _updates = new();

        public IReadOnlyList<ConversionProgress> Updates
        {
            get { lock (_gate) return _updates.ToArray(); }
        }

        public void Report(ConversionProgress value)
        {
            lock (_gate)
                _updates.Add(value);
        }
    }

    /// <summary>Read-only wrapper that cancels a token on its first read, making
    /// "Cancel arrives mid-file" deterministic instead of timing-dependent.</summary>
    private sealed class CancelOnFirstReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationTokenSource _cts;
        private int _cancelled;

        public CancelOnFirstReadStream(Stream inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        private void CancelOnce()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
                _cts.Cancel();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            CancelOnce();
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CancelOnce();
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
