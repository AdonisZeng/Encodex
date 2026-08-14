using System.IO;
using System.Text;
using Encodex.Models;

namespace Encodex.Services;

public class ConversionProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public string CurrentFile { get; init; } = "";
}

public class ConversionSummary
{
    public int Success { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Copied { get; init; }
    public string OutputPath { get; init; } = "";
}

/// <summary>Per-file outcome of copying unmatched files, for the detailed report.</summary>
public class UnmatchedCopyResult
{
    /// <summary>Relative paths of files copied successfully.</summary>
    public List<string> CopiedFiles { get; } = new();

    /// <summary>Relative paths of files that could not be copied.</summary>
    public List<string> FailedFiles { get; } = new();

    public int Copied => CopiedFiles.Count;
    public int Failed => FailedFiles.Count;
}

public class EncodingConverter
{
    private const int BufferSize = 64 * 1024;

    public async Task<ConversionSummary> ConvertAsync(
        List<FileConversionItem> items,
        string sourceDirectory,
        string outputDirectory,
        Encoding targetEncoding,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int success = 0, skipped = 0, failed = 0, copied = 0;

        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            progress?.Report(new ConversionProgress
            {
                Processed = i,
                Total = items.Count,
                CurrentFile = item.RelativePath
            });

            if (!item.IsSelected)
                continue;

            var sourcePath = Path.Combine(sourceDirectory, item.RelativePath);
            var outputPath = Path.Combine(outputDirectory, item.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            string? tempPath = null;
            try
            {
                var sourceEncoding = GetStrictEncodingFromName(item.DetectedEncoding);
                if (sourceEncoding == null)
                {
                    // Unknown encoding: nothing to convert, keep the file as-is.
                    CopyFile(sourcePath, outputPath);
                    item.Status = ConversionStatus.Copied;
                    // Preserve a scan-time reason (e.g. "二进制文件") when present.
                    item.StatusMessage ??= "检测失败，原样复制";
                    copied++;
                    continue;
                }

                var target = GetStrictEncoding(targetEncoding);
                var targetPreamble = targetEncoding.GetPreamble();
                var sourceHasBom = await HasBomAsync(sourcePath, cancellationToken);
                var targetHasBom = targetPreamble.Length > 0;

                // Same code page and same BOM intent: copying is exactly what a
                // conversion would produce, so skip the decode/re-encode round-trip.
                if (sourceEncoding.CodePage == target.CodePage && sourceHasBom == targetHasBom)
                {
                    CopyFile(sourcePath, outputPath);
                    item.Status = ConversionStatus.Skipped;
                    item.StatusMessage = "编码相同，原样复制";
                    skipped++;
                    continue;
                }

                tempPath = outputPath + ".tmp";
                await ConvertFileAsync(sourcePath, tempPath, sourceEncoding, target, targetPreamble, cancellationToken);
                MoveFile(tempPath, outputPath);
                tempPath = null;

                item.Status = ConversionStatus.Success;
                success++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DecoderFallbackException)
            {
                // Source bytes that cannot be decoded: copying is safer than emitting garbage.
                CopyFile(sourcePath, outputPath);
                item.Status = ConversionStatus.Copied;
                item.StatusMessage = "解码失败，原样复制";
                copied++;
            }
            catch (EncoderFallbackException)
            {
                // Characters the target encoding cannot represent: same treatment.
                CopyFile(sourcePath, outputPath);
                item.Status = ConversionStatus.Copied;
                item.StatusMessage = "目标编码无法表示部分字符，原样复制";
                copied++;
            }
            catch (Exception ex)
            {
                try { CopyFile(sourcePath, outputPath); } catch { }
                item.Status = ConversionStatus.Failed;
                item.StatusMessage = ex.Message;
                failed++;
            }
            finally
            {
                // Do not leave a half-written temp file behind (e.g. on cancellation).
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        progress?.Report(new ConversionProgress
        {
            Processed = items.Count,
            Total = items.Count,
            CurrentFile = ""
        });

        return new ConversionSummary
        {
            Success = success,
            Skipped = skipped,
            Failed = failed,
            Copied = copied,
            OutputPath = outputDirectory
        };
    }

    public async Task<UnmatchedCopyResult> CopyUnmatchedFilesAsync(
        string sourceDirectory,
        string outputDirectory,
        List<string> unmatchedFiles,
        IProgress<ConversionProgress>? progress = null,
        int progressOffset = 0,
        CancellationToken cancellationToken = default)
    {
        var result = new UnmatchedCopyResult();

        for (int i = 0; i < unmatchedFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = unmatchedFiles[i];
            progress?.Report(new ConversionProgress
            {
                Processed = progressOffset + i,
                Total = progressOffset + unmatchedFiles.Count,
                CurrentFile = file
            });

            var relativePath = PathHelper.GetRelativePath(sourceDirectory, file);
            var outputPath = Path.Combine(outputDirectory, relativePath);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                CopyFile(file, outputPath);
                result.CopiedFiles.Add(relativePath);
            }
            catch (Exception)
            {
                // One unreadable/locked file must not abort the whole copy pass.
                result.FailedFiles.Add(relativePath);
            }
        }

        return result;
    }

    /// <summary>
    /// Streaming decode → re-encode. Memory use stays constant regardless of file
    /// size. Strict exception fallbacks are used on both sides so that undecodable
    /// source bytes or unrepresentable characters surface as exceptions instead of
    /// silently becoming replacement characters (garbage output).
    /// </summary>
    private static async Task ConvertFileAsync(
        string sourcePath,
        string tempPath,
        Encoding sourceEncoding,
        Encoding targetEncoding,
        byte[] targetPreamble,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            sourcePath,
            sourceEncoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: BufferSize);

        // StreamReader does not consume the BOM when byte-order-mark detection is
        // disabled, so strip the U+FEFF character manually.
        if (reader.Peek() == '\uFEFF')
            reader.Read();

        using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        if (targetPreamble.Length > 0)
            await output.WriteAsync(targetPreamble, 0, targetPreamble.Length, cancellationToken);

        // The preamble was written above only when the user asked for one. The strict
        // target instance preserves the original BOM flag, but the writer skips its
        // own preamble once the stream position is past zero; for BOM-less targets the
        // cloned encoding's preamble is empty either way, so nothing extra is emitted.
        using var writer = new StreamWriter(output, targetEncoding, BufferSize, leaveOpen: false);
        var buffer = new char[BufferSize];
        int charsRead;
        // StreamReader.ReadAsync has no CancellationToken overload on .NET Framework;
        // cancellation is honored between files via ThrowIfCancellationRequested.
        while ((charsRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            await writer.WriteAsync(buffer, 0, charsRead);
        await writer.FlushAsync();
    }

    private static async Task<bool> HasBomAsync(string filePath, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4, useAsync: true);
        int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return true;
        if (read >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
            return true;
        if (read >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
            return true;
        if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF)))
            return true;
        return false;
    }

    private static Encoding? GetStrictEncodingFromName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        try
        {
            return Encoding.GetEncoding(
                name,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch
        {
            return null;
        }
    }

    private static Encoding GetStrictEncoding(Encoding encoding)
    {
        // Clone instead of Encoding.GetEncoding(codePage): a code-page lookup
        // loses the BOM flag (GetEncoding(65001) returns a BOM-emitting instance),
        // which would silently add a BOM to "UTF-8 without BOM" conversions.
        // Clone keeps the original BOM intent and returns a writable copy, so the
        // strict fallbacks can be applied to it.
        var strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        return strict;
    }

    private static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>.NET Framework has no File.Move overload with an overwrite flag.</summary>
    private static void MoveFile(string source, string destination)
    {
        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(source, destination);
    }
}
