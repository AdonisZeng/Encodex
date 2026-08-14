using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Encodex.Services;
using Xunit;

namespace Encodex.Tests.Services;

public class AppUpdateServiceTests
{
    // ---- command-line argument escaping ----

    [Fact]
    public void QuoteWindowsArgument_RoundTripsThroughWindowsParser()
    {
        var arguments = new[]
        {
            "12345",
            "C:\\temp\\Encodex-update-1.0.0.3.zip",
            "C:\\Program Files\\Encodex\\",   // trailing backslash — the original bug
            "D:\\Encodex\\",
            "C:\\dir with spaces\\sub\\",
            "plain",
            "quote\"inside",
        };

        var commandLine = string.Join(" ", arguments.Select(AppUpdateService.QuoteWindowsArgument));

        Assert.Equal(arguments, ParseCommandLine(commandLine));
    }

    [Fact]
    public void QuoteWindowsArgument_TrailingBackslashSurvives()
    {
        // AppContext.BaseDirectory always ends with a backslash; if it is not escaped,
        // the closing quote is read as an escaped quote and the argument is corrupted.
        var commandLine = "12345 C:\\temp\\z.zip " +
            AppUpdateService.QuoteWindowsArgument("C:\\Program Files\\Encodex\\");

        Assert.Equal("C:\\Program Files\\Encodex\\", ParseCommandLine(commandLine)[2]);
    }

    // ---- manifest parsing / version comparison ----

    [Fact]
    public async Task FetchLatestAsync_ParsesManifest()
    {
        var xml = "<update><version>9.9.9.9</version><url>http://x/z.zip</url><sha256>abc</sha256><mandatory>true</mandatory><notes>hello</notes></update>";

        var info = await CreateService(xml).FetchLatestAsync();

        Assert.Equal(new Version(9, 9, 9, 9), info.Version);
        Assert.Equal("http://x/z.zip", info.DownloadUrl);
        Assert.Equal("abc", info.Sha256);
        Assert.True(info.Mandatory);
        Assert.Equal("hello", info.Notes);
    }

    [Fact]
    public async Task FetchLatestAsync_MissingVersion_Throws()
    {
        var service = CreateService("<update><url>http://x/z.zip</url></update>");

        await Assert.ThrowsAsync<UpdateException>(() => service.FetchLatestAsync());
    }

    [Fact]
    public async Task FetchLatestAsync_InvalidXml_Throws()
    {
        var service = CreateService("this is not xml");

        await Assert.ThrowsAsync<UpdateException>(() => service.FetchLatestAsync());
    }

    [Fact]
    public async Task FetchLatestAsync_NetworkError_WrapsAsUpdateException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("no network"));
        var service = new AppUpdateService("http://localhost/update.xml", new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => service.FetchLatestAsync());
    }

    [Fact]
    public async Task CheckAsync_NewerVersion_ReturnsInfo()
    {
        var newer = new Version(AppUpdateService.CurrentVersion.Major + 1, 0, 0, 0);
        var service = CreateService($"<update><version>{newer}</version><url>http://x/z.zip</url></update>");

        var info = await service.CheckAsync();

        Assert.NotNull(info);
        Assert.Equal(newer, info.Version);
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReturnsNull()
    {
        var service = CreateService("<update><version>0.0.0.1</version><url>http://x/z.zip</url></update>");

        Assert.Null(await service.CheckAsync());
    }

    // ---- download & SHA-256 verification ----

    [Fact]
    public async Task DownloadAsync_Sha256Mismatch_ThrowsAndCleansUp()
    {
        var service = CreateDownloadService(Encoding.UTF8.GetBytes("hello update payload"));
        var info = new UpdateInfo
        {
            Version = new Version(9, 9, 9, 8),
            DownloadUrl = "http://x/z.zip",
            Sha256 = new string('0', 64)
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(info));

        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_Sha256CaseInsensitive_Succeeds()
    {
        var bytes = Encoding.UTF8.GetBytes("hello update payload");
        var sha = ComputeSha256Hex(bytes);
        var service = CreateDownloadService(bytes);
        var info = new UpdateInfo
        {
            Version = new Version(9, 9, 9, 9),
            DownloadUrl = "http://x/z.zip",
            Sha256 = sha.ToUpperInvariant()   // comparison must be case-insensitive
        };

        var path = await service.DownloadAsync(info);
        try
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgress()
    {
        var bytes = Encoding.UTF8.GetBytes("hello update payload");
        var service = CreateDownloadService(bytes);
        var info = new UpdateInfo
        {
            Version = new Version(9, 9, 9, 7),
            DownloadUrl = "http://x/z.zip",
            Sha256 = ComputeSha256Hex(bytes)
        };

        var collector = new CollectingProgress();
        var path = await service.DownloadAsync(info, collector);

        try
        {
            Assert.True(collector.Updates.Count > 0);
            Assert.Equal(bytes.Length, collector.Updates[collector.Updates.Count - 1].BytesReceived);
            Assert.Equal(bytes.Length, collector.Updates[collector.Updates.Count - 1].TotalBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- helpers ----

    private static AppUpdateService CreateService(string xml)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml)
        });
        return new AppUpdateService("http://localhost/update.xml", new HttpClient(handler));
    }

    private static AppUpdateService CreateDownloadService(byte[] bytes)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
        return new AppUpdateService("http://localhost/update.xml", new HttpClient(handler));
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                var ptr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(ptr)!;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Serves canned responses (or failures) without touching the network.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { return Task.FromResult(_respond(request)); }
            catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
        }
    }

    /// <summary>Synchronous IProgress collector (Progress&lt;T&gt; would marshal
    /// asynchronously without a SynchronizationContext and race the assertions).</summary>
    private sealed class CollectingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Updates { get; } = new();

        public void Report(DownloadProgress value) => Updates.Add(value);
    }
}
