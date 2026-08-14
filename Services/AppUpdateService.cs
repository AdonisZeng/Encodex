using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using Encodex.Resources;

namespace Encodex.Services;

/// <summary>
/// Checks the published update manifest (update.xml) for a newer version and applies
/// it by downloading the release zip, verifying its SHA-256 and handing the replacement
/// over to Encodex.Updater.exe (the running app cannot overwrite its own files).
/// </summary>
public class AppUpdateService
{
    private const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/AdonisZeng/Encodex/main/update.xml";

    private const string UpdaterFileName = "Encodex.Updater.exe";

    private static readonly HttpClient Http = CreateClient();

    private readonly string _manifestUrl;
    private readonly HttpClient _http;

    public AppUpdateService()
        : this(DefaultManifestUrl)
    {
    }

    /// <param name="manifestUrl">Custom manifest URL (used by tests).</param>
    internal AppUpdateService(string manifestUrl)
        : this(manifestUrl, Http)
    {
    }

    /// <param name="httpClient">Injected HTTP client (used by tests to serve canned responses).</param>
    internal AppUpdateService(string manifestUrl, HttpClient httpClient)
    {
        _manifestUrl = manifestUrl;
        _http = httpClient;
    }

    /// <summary>Version of the running application (AssemblyVersion in AssemblyInfo.cs).</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Downloads and parses the update manifest.</summary>
    public async Task<UpdateInfo> FetchLatestAsync()
    {
        string xml;
        try
        {
            xml = await _http.GetStringAsync(_manifestUrl);
        }
        catch (Exception ex)
        {
            throw new UpdateException(Res.Upd_NetworkError, ex);
        }

        try
        {
            var root = XDocument.Parse(xml).Root;
            return new UpdateInfo
            {
                Version = Version.Parse(ReadElement(root, "version") ?? throw new UpdateException(Res.Upd_MissingVersion)),
                DownloadUrl = ReadElement(root, "url") ?? throw new UpdateException(Res.Upd_MissingUrl),
                Sha256 = ReadElement(root, "sha256"),
                Mandatory = bool.TryParse(ReadElement(root, "mandatory"), out var m) && m,
                Notes = ReadElement(root, "notes") ?? ""
            };
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UpdateException(Res.Upd_InvalidXml, ex);
        }
    }

    /// <summary>Returns the manifest when a newer version exists, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        var info = await FetchLatestAsync();
        return info.Version > CurrentVersion ? info : null;
    }

    /// <summary>
    /// Downloads the release zip to a temp file and verifies its SHA-256
    /// (when the manifest provides one).
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress = null)
    {
        var tempZip = Path.Combine(
            Path.GetTempPath(), $"Encodex-update-{info.Version}.zip");
        if (File.Exists(tempZip))
            File.Delete(tempZip);

        try
        {
            using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var fileStream = File.Create(tempZip);
            await response.Content.CopyToAsync(new ProgressStream(fileStream, total, progress));
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempZip);
            throw new UpdateException(Res.Upd_DownloadFailed, ex);
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256) &&
            !string.Equals(ComputeSha256(tempZip), info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(tempZip);
            throw new UpdateException(Res.Upd_ChecksumFailed);
        }

        return tempZip;
    }

    /// <summary>
    /// Launches Encodex.Updater.exe with (currentPid, zipPath, installDirectory)
    /// and shuts the app down so the updater can replace the files.
    /// </summary>
    public void ApplyUpdateAndExit(string zipPath)
    {
        var installDirectory = AppContext.BaseDirectory;
        var updaterPath = Path.Combine(installDirectory, UpdaterFileName);
        if (!File.Exists(updaterPath))
            throw new UpdateException(string.Format(Res.Upd_MissingUpdater, UpdaterFileName));

        // Each argument is escaped for the Windows command-line parser. installDirectory
        // (AppContext.BaseDirectory) always ends with a backslash, which would otherwise
        // escape the closing quote and corrupt the final argument.
        var arguments = string.Join(" ", new[]
        {
            QuoteWindowsArgument(Process.GetCurrentProcess().Id.ToString()),
            QuoteWindowsArgument(zipPath),
            QuoteWindowsArgument(installDirectory)
        });
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = arguments,
            UseShellExecute = false
        }) ?? throw new UpdateException(string.Format(Res.Upd_LaunchFailed, UpdaterFileName));

        // Exit after a short delay so the updater can reliably wait for this process.
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Threading.Thread.Sleep(300);
            Application.Current.Shutdown();
        }));
    }

    /// <summary>
    /// Checks for leftovers of a failed update attempt: an updater that died while
    /// self-replacing leaves the extracted replacement next to the running exe.
    /// Returns a user-presentable message, or null when there is no trace of failure.
    /// </summary>
    public static string? DetectFailedUpdate()
    {
        try
        {
            var leftover = Path.Combine(AppContext.BaseDirectory, UpdaterFileName + ".new");
            if (File.Exists(leftover))
            {
                File.Delete(leftover);
                return Res.Upd_FailedUpdateLeftover;
            }
        }
        catch
        {
            // Diagnostics only — must never break startup.
        }

        return null;
    }

    private static string? ReadElement(XElement? root, string name) =>
        root?.Element(name)?.Value.Trim();

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Quotes a single argument for the Windows command-line parser, following the
    /// same rules CommandLineToArgvW and the CLR use. Trailing backslashes are doubled
    /// so they do not escape the closing quote, and embedded quotes are backslash-escaped.
    /// </summary>
    internal static string QuoteWindowsArgument(string argument)
    {
        // No quoting is needed when the argument contains no separators or quotes.
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            return argument;

        var result = new StringBuilder();
        result.Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // End of argument: double the backslashes before the closing quote.
                result.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"')
            {
                // Escaped quote: one extra backslash plus the literal quote.
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashes);
                result.Append(argument[i]);
            }
        }
        result.Append('"');
        return result.ToString();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // raw.githubusercontent.com rejects requests without a User-Agent header.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Encodex");
        return client;
    }
}

/// <summary>Parsed content of the published update.xml manifest.</summary>
public class UpdateInfo
{
    public Version Version { get; set; } = new(0, 0);
    public string DownloadUrl { get; set; } = "";
    public string? Sha256 { get; set; }
    public bool Mandatory { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Download progress: bytes received so far and the total when known.</summary>
public class DownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
}

/// <summary>Wraps a writable stream and reports bytes written, so the update download
/// can surface progress without buffering the whole payload.</summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long? _total;
    private readonly IProgress<DownloadProgress>? _progress;
    private long _received;

    public ProgressStream(Stream inner, long? total, IProgress<DownloadProgress>? progress)
    {
        _inner = inner;
        _total = total;
        _progress = progress;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Report(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer, offset, count, cancellationToken);
        Report(count);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Report(int count)
    {
        _received += count;
        _progress?.Report(new DownloadProgress { BytesReceived = _received, TotalBytes = _total });
    }
}

/// <summary>User-presentable update failure; Message is safe to show directly.</summary>
public class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
