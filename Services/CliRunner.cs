using System.IO;
using System.Text;
using Encodex.Models;
using Encodex.Resources;

namespace Encodex.Services;

/// <summary>
/// Headless batch-conversion entry point, for CI/script integration:
/// Encodex.exe --cli --src &lt;folder&gt; --target &lt;encoding&gt; [--out &lt;dir&gt; | --overwrite] [--ext .cs,.js]
/// </summary>
public static class CliRunner
{
    public static int Run(string[] args, TextWriter? output = null)
    {
        // Script-friendly output: wrap the raw stdout stream in a UTF-8 writer.
        // (Console.OutputEncoding cannot be changed when output is redirected on
        // .NET Framework, and the default console code page mangles non-ASCII.)
        // The internally created writer is disposed so the output stream is flushed
        // and released; a caller-supplied writer stays owned by the caller.
        var ownsWriter = output == null;
        var writer = output ?? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        try
        {
            return RunCore(args, writer);
        }
        finally
        {
            if (ownsWriter)
                writer.Dispose();
        }
    }

    private static int RunCore(string[] args, TextWriter writer)
    {
        string? sourceDir = null, targetName = null, outDir = null;
        var extensions = new List<string>();
        bool overwrite = false;

        // An omitted --ext means "use the built-in defaults", while an explicit
        // --ext with an empty payload means "the user asked for a filter that
        // matches nothing". Distinguishing the two prevents the old silent
        // fallback that converted the whole tree with default rules instead of
        // honouring the user's (broken) filter. If the flag is the last token,
        // that is a user error rather than an omitted option, so report it.
        bool sawExtOption = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--src":
                {
                    if (!TryReadValue(args, ref i, out var value))
                        return ReportMissingValue(writer, "--src");
                    sourceDir = value;
                    break;
                }
                case "--target":
                {
                    if (!TryReadValue(args, ref i, out var value))
                        return ReportMissingValue(writer, "--target");
                    targetName = value;
                    break;
                }
                case "--out":
                {
                    // Falling back to the auto-named folder here used to hide the
                    // typo and scatter output into an unexpected location. An explicit
                    // empty value ("") is the same user error as an omitted one.
                    if (!TryReadValue(args, ref i, out var value) || string.IsNullOrWhiteSpace(value))
                        return ReportMissingValue(writer, "--out");
                    outDir = value;
                    break;
                }
                case "--ext":
                {
                    if (!TryReadValue(args, ref i, out var value))
                        return ReportMissingValue(writer, "--ext");
                    sawExtOption = true;
                    extensions.AddRange(value.Split(
                        new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                    break;
                }
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--help":
                case "-h":
                    PrintUsage(writer);
                    return 0;
                default:
                    writer.WriteLine(string.Format(Res.Cli_UnknownArg, args[i]));
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            writer.WriteLine(Res.Cli_InvalidSrc);
            return 2;
        }
        if (string.IsNullOrWhiteSpace(targetName))
        {
            writer.WriteLine(Res.Cli_MissingTarget);
            return 2;
        }
        var targetEncoding = ResolveTargetEncoding(targetName!);
        if (targetEncoding == null)
        {
            writer.WriteLine(string.Format(Res.Cli_UnsupportedTarget, targetName));
            return 2;
        }
        if (overwrite && !string.IsNullOrEmpty(outDir))
        {
            writer.WriteLine(Res.Cli_ConflictOut);
            return 2;
        }

        var profile = new ExtensionProfile(loadDefaults: false);
        if (!sawExtOption)
        {
            foreach (var ext in ExtensionProfile.GetDefaultExtensions())
                profile.AddExtension(ext);
        }
        else
        {
            foreach (var ext in extensions)
            {
                if (!profile.AddExtension(ext))
                    writer.WriteLine(string.Format(Res.Cli_InvalidExt, ext));
            }
            // sawExtOption but nothing usable survived ("--ext" alone, `""` or
            // ","): abort instead of quietly scanning everything by default.
            if (profile.Extensions.Count == 0)
            {
                writer.WriteLine(Res.Cli_NoValidExt);
                return 2;
            }
        }

        // Scan + detect.
        var scanner = new FileScanner();
        var detector = new EncodingDetector();
        var scan = scanner.ScanAll(sourceDir!, profile);
        var items = new List<FileConversionItem>();
        foreach (var file in scan.Matched)
        {
            // A file that vanished or turned unreadable between the scan and this
            // read must not crash the batch: degrade it to "unknown", which the
            // converter copies as-is.
            DetectionResult detection;
            try
            {
                detection = detector.Detect(file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detection = new DetectionResult();
            }
            items.Add(new FileConversionItem
            {
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                FileSize = file.FileSize,
                DetectedEncoding = detection.EncodingName,
                TargetEncoding = targetEncoding.WebName,
                IsBinary = detection.IsBinary,
                Status = detection.IsBinary ? ConversionStatus.Skipped : ConversionStatus.Pending,
                StatusMessage = detection.IsBinary ? Res.VM_Binary : null
            });
        }

        string targetDir;
        if (overwrite)
        {
            targetDir = sourceDir!;
        }
        else if (!string.IsNullOrEmpty(outDir))
        {
            targetDir = outDir!;
        }
        else
        {
            // A drive root ("C:\", "D:") has no parent directory: GetDirectoryName
            // returns null there, so place the output inside the source folder instead.
            var trimmed = sourceDir!.TrimEnd('\\');
            var parent = Path.GetDirectoryName(trimmed);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            {
                targetDir = Path.Combine(sourceDir!, "Encodex_" + EncodingConverter.GetEncodingFolderName(targetEncoding));
            }
            else
            {
                targetDir = Path.Combine(parent, $"{name}_{EncodingConverter.GetEncodingFolderName(targetEncoding)}");
            }
        }

        // Run the async conversion on the thread pool: awaiting it synchronously on a
        // UI-thread SynchronizationContext (WPF Dispatcher) would deadlock, because the
        // continuation needs the very thread that GetResult() is blocking.
        var summary = Task.Run(() => Converter.ConvertAsync(
            items, sourceDir!, targetDir!, targetEncoding, overwriteInPlace: overwrite))
            .GetAwaiter().GetResult();

        writer.WriteLine(string.Format(Res.Cli_ScanSummary, items.Count, scan.Unmatched.Count));
        writer.WriteLine(string.Format(Res.Cli_ConvertSummary,
            summary.Success, summary.Skipped, summary.Failed, summary.Copied));
        if (!string.IsNullOrEmpty(summary.BackupDirectory))
            writer.WriteLine(string.Format(Res.Cli_BackupDir, summary.BackupDirectory));

        foreach (var item in items.Where(i => i.Status == ConversionStatus.Failed))
            writer.WriteLine(string.Format(Res.Cli_FailedFile, item.RelativePath, item.StatusMessage));

        return summary.Failed > 0 ? 1 : 0;
    }

    private static readonly EncodingConverter Converter = new();

    /// <summary>
    /// Reads the value following an option flag. Returns false and leaves
    /// <paramref name="index"/> untouched when the flag is the last token, so
    /// callers can report a precise "missing value" error instead of silently
    /// treating the option as if it were never supplied.
    /// </summary>
    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = "";
            return false;
        }
        index++;
        value = args[index];
        return true;
    }

    private static int ReportMissingValue(TextWriter writer, string option)
    {
        writer.WriteLine(string.Format(Res.Cli_MissingValue, option));
        return 2;
    }

    private static Encoding? ResolveTargetEncoding(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "utf8":
            case "utf-8":
                return new UTF8Encoding(false);
            case "utf8-bom":
            case "utf-8-bom":
                return new UTF8Encoding(true);
            case "utf16le":
            case "utf-16le":
            case "utf-16":
                return new UnicodeEncoding(false, false);
            case "utf16be":
            case "utf-16be":
                return new UnicodeEncoding(true, false);
            case "utf32le":
            case "utf-32le":
            case "utf-32":
                return new UTF32Encoding(false, false);
            case "utf32be":
            case "utf-32be":
                return new UTF32Encoding(true, false);
            case "gbk":
                return Encoding.GetEncoding("GBK");
            case "gb2312":
                return Encoding.GetEncoding("GB2312");
            case "gb18030":
                return Encoding.GetEncoding("GB18030");
            case "big5":
                return Encoding.GetEncoding("big5");
            case "shift-jis":
            case "shift_jis":
            case "sjis":
                return Encoding.GetEncoding("shift_jis");
            case "euc-kr":
                return Encoding.GetEncoding("euc-kr");
            case "ascii":
                return Encoding.ASCII;
            case "iso-8859-1":
            case "latin1":
            case "latin-1":
                return Encoding.GetEncoding("ISO-8859-1");
            default:
                return null;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine(Res.Cli_HelpTitle);
        writer.WriteLine(Res.Cli_HelpUsage);
        writer.WriteLine();
        writer.WriteLine(Res.Cli_HelpRequired);
        writer.WriteLine(Res.Cli_HelpSrc);
        writer.WriteLine(Res.Cli_HelpTarget);
        writer.WriteLine(Res.Cli_HelpTarget2);
        writer.WriteLine(Res.Cli_HelpTarget3);
        writer.WriteLine(Res.Cli_HelpOptions);
        writer.WriteLine(Res.Cli_HelpOut);
        writer.WriteLine(Res.Cli_HelpOverwrite);
        writer.WriteLine(Res.Cli_HelpExt);
        writer.WriteLine(Res.Cli_HelpHelp);
    }
}
