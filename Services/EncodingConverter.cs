using System.IO;
using System.Text;
using Encodex.Models;
using Encodex.Resources;

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

    /// <summary>Set when converting in place: where the originals were backed up.</summary>
    public string? BackupDirectory { get; init; }
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

    /// <summary>Cap on concurrent file conversions: disk-bound work does not scale
    /// past a handful of parallel streams. Scanning, backing up and copying unmatched
    /// files reuse the same cap so every phase behaves the same on the same disk.</summary>
    internal const int MaxConcurrency = 8;

    /// <summary>Outcome of converting one file, used to aggregate summary counters.</summary>
    private enum ConvertOutcome
    {
        Success,
        Skipped,
        Copied,
        Failed
    }

    public async Task<ConversionSummary> ConvertAsync(
        List<FileConversionItem> items,
        string sourceDirectory,
        string outputDirectory,
        Encoding targetEncoding,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool overwriteInPlace = false,
        int progressOffset = 0)
    {
        var selected = items.Where(i => i.IsSelected).ToList();

        // In-place mode: back up every file that may be rewritten before touching any
        // of them, so a partial failure never leaves the originals unrecoverable.
        string? backupDirectory = null;
        if (overwriteInPlace)
            backupDirectory = await BackupFilesAsync(selected, sourceDirectory, progress, cancellationToken);

        // Files whose backup failed must never be rewritten in place (the backup is
        // the only recovery path), so they are excluded from the conversion pass.
        var convertList = overwriteInPlace
            ? selected.Where(i => i.Status != ConversionStatus.Failed).ToList()
            : selected;

        int success = 0, skipped = 0, failed = 0, copied = 0, processed = 0;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(Environment.ProcessorCount, MaxConcurrency)));
        var tasks = new List<Task>(convertList.Count);

        // Each file is independent (distinct source and output), so files convert in
        // parallel under a concurrency cap. Counter updates are atomic via Interlocked;
        // progress is aggregated as files complete.
        foreach (var item in convertList)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var outcome = await ConvertOneAsync(
                        item, sourceDirectory, outputDirectory, targetEncoding,
                        overwriteInPlace, cancellationToken);
                    switch (outcome)
                    {
                        case ConvertOutcome.Success: Interlocked.Increment(ref success); break;
                        case ConvertOutcome.Skipped: Interlocked.Increment(ref skipped); break;
                        case ConvertOutcome.Copied: Interlocked.Increment(ref copied); break;
                        case ConvertOutcome.Failed: Interlocked.Increment(ref failed); break;
                    }
                }
                finally
                {
                    semaphore.Release();
                }

                var done = Interlocked.Increment(ref processed);
                progress?.Report(new ConversionProgress
                {
                    Processed = progressOffset + done,
                    Total = progressOffset + convertList.Count,
                    CurrentFile = item.RelativePath
                });
            }, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException (canceled tasks) derives from OperationCanceledException;
            // individual files never throw other exceptions (all handled in ConvertOneAsync).
            throw;
        }

        // No terminal report here: the per-file reports already reach
        // progressOffset + convertList.Count, and this method does not know the
        // caller's global maximum (backup phase + unmatched copies). The caller
        // snaps the bar to full once every phase has finished.

        return new ConversionSummary
        {
            Success = success,
            Skipped = skipped,
            Failed = failed,
            Copied = copied,
            OutputPath = overwriteInPlace ? sourceDirectory : outputDirectory,
            BackupDirectory = backupDirectory
        };
    }

    /// <summary>Copies every selected source file into a fresh temp backup folder,
    /// preserving the relative directory structure. Per-file failures (locked,
    /// unreadable) mark the item Failed so it is excluded from the in-place rewrite
    /// instead of aborting the whole backup pass. Progress covers the backup phase
    /// (offset 0); the conversion phase continues from ConvertAsync's progressOffset.
    /// Files are copied concurrently: the pass is disk-bound and a serial walk
    /// dominates the total time on large trees.</summary>
    private static async Task<string?> BackupFilesAsync(
        List<FileConversionItem> items,
        string sourceDirectory,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The timestamp is only second-precise: two conversions started within the
        // same second must not share (and overwrite) one backup folder.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"Encodex-backup-{stamp}");
        for (int suffix = 2; Directory.Exists(backupDirectory); suffix++)
            backupDirectory = Path.Combine(Path.GetTempPath(), $"Encodex-backup-{stamp}-{suffix}");
        Directory.CreateDirectory(backupDirectory);

        int processed = 0;
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>(items.Count);

        foreach (var item in items)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Skipped items (binary files) are never rewritten in place, so
                    // copying them would be wasted IO; they still advance progress so
                    // the caller's backup/progress offset chain stays intact.
                    if (item.Status != ConversionStatus.Skipped)
                    {
                        var sourcePath = Path.Combine(sourceDirectory, item.RelativePath);
                        var backupPath = Path.Combine(backupDirectory, item.RelativePath);
                        try
                        {
                            // Concurrent CreateDirectory calls on the same path are idempotent.
                            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                            File.Copy(sourcePath, backupPath, overwrite: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            item.Status = ConversionStatus.Failed;
                            item.StatusMessage = Res.Conv_BackupFailed;
                        }
                    }

                    var done = Interlocked.Increment(ref processed);
                    progress?.Report(new ConversionProgress
                    {
                        Processed = done,
                        Total = items.Count,
                        CurrentFile = item.RelativePath
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        return backupDirectory;
    }

    private static async Task<ConvertOutcome> ConvertOneAsync(
        FileConversionItem item,
        string sourceDirectory,
        string outputDirectory,
        Encoding targetEncoding,
        bool overwriteInPlace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = Path.Combine(sourceDirectory, item.RelativePath);
        var outputPath = overwriteInPlace
            ? sourcePath
            : Path.Combine(outputDirectory, item.RelativePath);

        // Ensure the destination folder exists up front: every branch below
        // (copy-as-is, skip, convert, failure fallback) writes into it.
        if (!overwriteInPlace)
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string? tempPath = null;
        try
        {
            var sourceEncoding = GetStrictEncodingFromName(item.DetectedEncoding);
            if (sourceEncoding == null)
            {
                // Unknown encoding: nothing to convert, keep the file as-is.
                if (!overwriteInPlace)
                    CopyFile(sourcePath, outputPath);
                // Binary files were already Skipped at scan time: an untouched file
                // must not be re-reported as "copied".
                if (item.Status != ConversionStatus.Skipped)
                    item.Status = ConversionStatus.Copied;
                // Preserve a scan-time reason (e.g. "二进制文件") when present.
                item.StatusMessage ??= Res.Conv_UnknownCopy;
                return item.Status == ConversionStatus.Skipped
                    ? ConvertOutcome.Skipped
                    : ConvertOutcome.Copied;
            }

            var target = GetStrictEncoding(targetEncoding);
            var targetPreamble = targetEncoding.GetPreamble();
            var targetHasBom = targetPreamble.Length > 0;

            // Single open per file: the head bytes answer the BOM question and the
            // same handle then feeds either the straight copy or the conversion.
            // (Previously the BOM check opened the file a second time.)
            // The handle must be released before the temp file can replace the
            // source — in-place mode rewrites the very same path.
            FileStream? sourceStream = null;
            try
            {
                sourceStream = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
                var sourceHasBom = await DetectBomAsync(sourceStream, cancellationToken);
                sourceStream.Seek(0, SeekOrigin.Begin);

                // Same code page and same BOM intent: copying is exactly what a
                // conversion would produce, so skip the decode/re-encode round-trip.
                if (sourceEncoding.CodePage == target.CodePage && sourceHasBom == targetHasBom)
                {
                    if (!overwriteInPlace)
                    {
                        await CopyStreamAsync(sourceStream, outputPath, cancellationToken);
                        PreserveTimestamps(sourcePath, outputPath);
                    }
                    item.Status = ConversionStatus.Skipped;
                    item.StatusMessage = Res.Conv_SameCopy;
                    return ConvertOutcome.Skipped;
                }

                // The source is gone once the temp file replaces it, so its timestamps
                // must be captured before the rewrite — reading them afterwards would
                // return the rewritten file's own (current) timestamps.
                var capturedTimestamps = overwriteInPlace ? TryCaptureTimestamps(sourcePath) : null;

                tempPath = outputPath + ".tmp";
                await ConvertFileAsync(sourceStream, tempPath, sourceEncoding, target, targetPreamble, cancellationToken);

                sourceStream.Dispose();
                sourceStream = null;

                ReplaceFile(tempPath, outputPath);
                tempPath = null;
                if (capturedTimestamps is { } timestamps)
                    ApplyTimestamps(timestamps, outputPath);
                else
                    PreserveTimestamps(sourcePath, outputPath);
            }
            finally
            {
                sourceStream?.Dispose();
            }

            item.Status = ConversionStatus.Success;
            return ConvertOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            // Source bytes that cannot be decoded: copying is safer than emitting garbage.
            if (!overwriteInPlace)
                CopyFile(sourcePath, outputPath);
            item.Status = ConversionStatus.Copied;
            item.StatusMessage = Res.Conv_DecodeFailed;
            return ConvertOutcome.Copied;
        }
        catch (EncoderFallbackException)
        {
            // Characters the target encoding cannot represent: same treatment.
            if (!overwriteInPlace)
                CopyFile(sourcePath, outputPath);
            item.Status = ConversionStatus.Copied;
            item.StatusMessage = Res.Conv_EncodeFailed;
            return ConvertOutcome.Copied;
        }
        catch (Exception ex)
        {
            if (!overwriteInPlace)
            {
                try { CopyFile(sourcePath, outputPath); } catch { }
            }
            item.Status = ConversionStatus.Failed;
            item.StatusMessage = ex.Message;
            return ConvertOutcome.Failed;
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

    public async Task<UnmatchedCopyResult> CopyUnmatchedFilesAsync(
        string sourceDirectory,
        string outputDirectory,
        List<string> unmatchedFiles,
        IProgress<ConversionProgress>? progress = null,
        int progressOffset = 0,
        CancellationToken cancellationToken = default)
    {
        var result = new UnmatchedCopyResult();
        if (unmatchedFiles.Count == 0)
            return result;

        // Unmatched files are typically the bulk of a tree's bytes (images, videos,
        // build artifacts), so they copy concurrently. Outcomes land in per-index
        // slots so the report keeps the input order without any locking.
        var relativePaths = new string[unmatchedFiles.Count];
        var copied = new bool[unmatchedFiles.Count];
        int processed = 0;
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>(unmatchedFiles.Count);

        for (int i = 0; i < unmatchedFiles.Count; i++)
        {
            int index = i;
            var file = unmatchedFiles[index];
            relativePaths[index] = PathHelper.GetRelativePath(sourceDirectory, file);

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var outputPath = Path.Combine(outputDirectory, relativePaths[index]);
                        // Concurrent CreateDirectory calls on the same path are idempotent.
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                        CopyFile(file, outputPath);
                        copied[index] = true;
                    }
                    catch (Exception)
                    {
                        // One unreadable/locked file must not abort the whole copy pass.
                    }

                    var done = Interlocked.Increment(ref processed);
                    // Reported after the attempt so the last value is exactly
                    // offset + count and the bar never stalls one short.
                    progress?.Report(new ConversionProgress
                    {
                        Processed = progressOffset + done,
                        Total = progressOffset + unmatchedFiles.Count,
                        CurrentFile = file
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        for (int i = 0; i < unmatchedFiles.Count; i++)
        {
            if (copied[i])
                result.CopiedFiles.Add(relativePaths[i]);
            else
                result.FailedFiles.Add(relativePaths[i]);
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
        Stream sourceStream,
        string tempPath,
        Encoding sourceEncoding,
        Encoding targetEncoding,
        byte[] targetPreamble,
        CancellationToken cancellationToken)
    {
        // The caller owns the stream (it was opened once for both the BOM check and
        // this conversion), so it must be left open here.
        using var reader = new StreamReader(
            sourceStream,
            sourceEncoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: BufferSize,
            leaveOpen: true);

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

    /// <summary>Folder name for an output directory; distinguishes BOM variants that
    /// share the same WebName (UTF-8 with/without BOM are both "utf-8"). Shared by the
    /// GUI and the CLI so both name their output folders identically.</summary>
    public static string GetEncodingFolderName(Encoding encoding)
        => encoding.GetPreamble().Length > 0 ? encoding.WebName + "-bom" : encoding.WebName;

    /// <summary>Reads the head of an already-open stream and reports whether it starts
    /// with a byte-order mark. The caller rewinds the stream afterwards, so the same
    /// handle can be reused for the actual conversion.</summary>
    private static async Task<bool> DetectBomAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
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

    /// <summary>Streams the source straight into the output without decoding, for the
    /// skip path; reuses the already-open handle so the file is only read once.</summary>
    private static async Task CopyStreamAsync(Stream source, string destinationPath, CancellationToken cancellationToken)
    {
        using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await source.CopyToAsync(destination, BufferSize, cancellationToken);
    }

    private static Encoding? GetStrictEncodingFromName(string? name)
    {
        var encoding = EncodingDetector.ResolveName(name);
        if (encoding == null)
            return null;
        return GetStrictEncoding(encoding);
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
        PreserveTimestamps(source, destination);
    }

    /// <summary>File.Copy/rewrite reset the destination timestamps; carry the
    /// source timestamps over so backups and converted outputs stay recognizable.</summary>
    private static void PreserveTimestamps(string source, string destination)
    {
        var captured = TryCaptureTimestamps(source);
        if (captured is { } timestamps)
            ApplyTimestamps(timestamps, destination);
    }

    /// <summary>Reads a file's timestamps so they survive an in-place rewrite: once the
    /// file has been replaced, reading it again yields the rewritten file's own values.</summary>
    private static (DateTime CreationUtc, DateTime LastWriteUtc, DateTime LastAccessUtc)? TryCaptureTimestamps(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return (info.CreationTimeUtc, info.LastWriteTimeUtc, info.LastAccessTimeUtc);
        }
        catch
        {
            // Best effort: a locked or exotic file must not fail the conversion.
            return null;
        }
    }

    private static void ApplyTimestamps(
        (DateTime CreationUtc, DateTime LastWriteUtc, DateTime LastAccessUtc) timestamps,
        string destination)
    {
        try
        {
            var destInfo = new FileInfo(destination);
            destInfo.CreationTimeUtc = timestamps.CreationUtc;
            destInfo.LastWriteTimeUtc = timestamps.LastWriteUtc;
            destInfo.LastAccessTimeUtc = timestamps.LastAccessUtc;
        }
        catch
        {
            // Best effort: a locked or exotic file must not fail the conversion.
        }
    }

    /// <summary>
    /// Replaces the destination with the freshly written file. <see cref="File.Replace"/>
    /// is atomic — the destination is never momentarily absent, so a failure mid-swap
    /// cannot leave the user without the original file. It throws when the destination
    /// does not exist, in which case a plain move is enough.
    /// </summary>
    private static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination))
            File.Replace(source, destination, destinationBackupFileName: null);
        else
            File.Move(source, destination);
    }
}
