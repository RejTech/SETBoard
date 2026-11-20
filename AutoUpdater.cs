using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace SETBoard;

/// <summary>
/// 自动更新器：从 GitHub Releases 检测新版本，支持多镜像源自动测速切换。
/// </summary>
internal static class AutoUpdater
{
    // ── GitHub 仓库信息 ───────────────────────────────
    private const string Owner = "RejTech";
    private const string Repo = "SETBoard";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    // ── 更新脚本地址（Pages 优先，raw 回退） ─────────────
    private static readonly string[] UpdaterScriptUrls =
    {
        "https://rejtech.github.io/SETBoard/Updater.ps1",
        "https://v4.gh-proxy.org/https://raw.githubusercontent.com/RejTech/SETBoard/main/Page/Updater.ps1",
        "https://raw.githubusercontent.com/RejTech/SETBoard/main/Page/Updater.ps1",
        "https://raw.githubusercontent.com/RejTech/SETBoard/master/Page/Updater.ps1",
    };

    // ── 状态 ──────────────────────────────────────────
    private static Version? _latestVersion;
    private static string? _downloadUrl;
    private static string? _releaseNotes;
    private static bool _checking;

    /// <summary>是否有新版本可用。</summary>
    public static bool HasUpdate => _latestVersion is not null && IsNewer();

    /// <summary>检测更新（异步）。完成后回调 result。</summary>
    public static async Task<bool> CheckAsync(Action<bool> completed)
    {
        if (_checking) return false;
        _checking = true;

        try
        {
            var local = GetCurrentVersion();
            var remote = await FetchLatestRelease();
            if (remote is null)
            {
                completed(false);
                return false;
            }

            _latestVersion = remote.Version;
            _downloadUrl = remote.DownloadUrl;
            _releaseNotes = remote.Notes;

            bool newer = IsNewer();
            if (!newer)
            {
                // 版本一致，清理缓存
                _latestVersion = null;
                _downloadUrl = null;
                _releaseNotes = null;
            }
            completed(newer);
            return newer;
        }
        catch
        {
            completed(false);
            return false;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>下载 Updater.ps1 并交给外部 PowerShell 进程执行，随后退出本进程。</summary>
    public static async Task<bool> DownloadAndApplyAsync(Action<double> progress, Action<string>? status)
    {
        if (_latestVersion is null) return false;

        try
        {
            status?.Invoke("正在下载更新脚本…");
            progress(-1);

            string? script = null;
            foreach (var url in UpdaterScriptUrls)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var text = await client.GetStringAsync(url);
                    if (!string.IsNullOrWhiteSpace(text) && text.Contains("SETBoard"))
                    {
                        script = text;
                        break;
                    }
                }
                catch { }
            }

            if (script is null)
            {
                status?.Invoke("更新脚本下载失败，请检查网络后重试");
                return false;
            }

            string path = Path.Combine(Path.GetTempPath(), "SETBoard-Updater.ps1");
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));

            status?.Invoke("已启动外部更新程序：下载源码并构建，期间请勿关闭 PowerShell 窗口…");
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                UseShellExecute = true, // 显示窗口，构建进度可见
            });

            // 主动退出：通知守护进程不要重启，由外部脚本负责构建替换并重启
            Watchdog.SignalAllowExit();
            await Task.Delay(300);
            Environment.Exit(0);
            return true;
        }
        catch
        {
            status?.Invoke("无法启动更新程序，请手动更新");
            return false;
        }
    }

    // ── 内部实现 ──────────────────────────────────────

    private static Version GetCurrentVersion()
    {
        try
        {
            using var stream = typeof(AutoUpdater).Assembly
                .GetManifestResourceStream("SETBoard.version.json");
            if (stream is not null)
            {
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("version", out var v)
                    && v.GetString() is { Length: > 0 } text
                    && Version.TryParse(text, out var ver))
                    return ver;
            }
        }
        catch { }

        var asm = typeof(AutoUpdater).Assembly.GetName().Version;
        return asm ?? new Version(1, 0, 0);
    }

    private static bool IsNewer()
    {
        if (_latestVersion is null) return false;
        var local = GetCurrentVersion();
        return _latestVersion > local;
    }

    private record LatestReleaseData(Version Version, string DownloadUrl, string Notes);

    private static async Task<LatestReleaseData?> FetchLatestRelease()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SETBoard", "1.0"));

            using var response = await client.GetAsync(ApiUrl);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 取 tag_name，格式如 "v1.0.5"
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("v")) return null;

            if (!Version.TryParse(tag.AsSpan(1), out var ver)) return null;

            // 找 win-x64.zip 下载链接
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip"))
                {
                    // 保留完整下载地址，镜像模板在其前面拼接加速前缀
                    string browserUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    return new LatestReleaseData(ver, browserUrl, root.GetProperty("body").GetString() ?? "");
                }
            }
        }
        catch { }
        return null;
    }

    // ── 公共查询 API（供 UI 调用） ────────────────────
    public static Version? GetLatestVersion() => _latestVersion;
    public static string? GetReleaseNotes() => _releaseNotes;
    public static bool IsChecking() => _checking;
}
