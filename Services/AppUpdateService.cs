using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Xml.Linq;

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

    public AppUpdateService()
        : this(DefaultManifestUrl)
    {
    }

    /// <param name="manifestUrl">Custom manifest URL (used by tests).</param>
    internal AppUpdateService(string manifestUrl)
    {
        _manifestUrl = manifestUrl;
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
            xml = await Http.GetStringAsync(_manifestUrl);
        }
        catch (Exception ex)
        {
            throw new UpdateException("无法获取版本信息，请检查网络连接。", ex);
        }

        try
        {
            var root = XDocument.Parse(xml).Root;
            return new UpdateInfo
            {
                Version = Version.Parse(ReadElement(root, "version") ?? throw new UpdateException("update.xml 缺少 version 节点")),
                DownloadUrl = ReadElement(root, "url") ?? throw new UpdateException("update.xml 缺少 url 节点"),
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
            throw new UpdateException("update.xml 内容无效，无法解析。", ex);
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
    public async Task<string> DownloadAsync(UpdateInfo info)
    {
        var tempZip = Path.Combine(
            Path.GetTempPath(), $"Encodex-update-{info.Version}.zip");
        if (File.Exists(tempZip))
            File.Delete(tempZip);

        try
        {
            using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var fileStream = File.Create(tempZip);
            await response.Content.CopyToAsync(fileStream);
        }
        catch (Exception ex)
        {
            throw new UpdateException("下载更新包失败，请检查网络连接。", ex);
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256) && ComputeSha256(tempZip) != info.Sha256)
        {
            throw new UpdateException("更新包校验失败（SHA-256 不一致），文件可能已损坏，请重试。");
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
            throw new UpdateException($"未找到 {UpdaterFileName}，无法执行更新。");

        // Quote all arguments: install paths may contain spaces.
        var arguments = $"\"{Process.GetCurrentProcess().Id}\" \"{zipPath}\" \"{installDirectory}\"";
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = arguments,
            UseShellExecute = false
        }) ?? throw new UpdateException($"无法启动 {UpdaterFileName}。");

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
                return "上一次自动更新未能完成安装（更新器在替换文件时中断），当前可能仍是旧版本。\n请再次点击 🔄 检查更新重试，或从 GitHub Releases 手动下载最新版本。";
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

/// <summary>User-presentable update failure; Message is safe to show directly.</summary>
public class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
