using System.Diagnostics;
using System.Net.Http.Headers;
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

    // ── 下载镜像源（按优先级排列，首个为首选加速站，最后为直连） ────
    private static readonly (string Name, string UrlTemplate)[] Mirrors =
    {
        ("gh-proxy",  "https://v4.gh-proxy.org/{0}"),
        ("ghfast",    "https://ghfast.top/{0}"),
        ("ghproxy",   "https://ghproxy.com/{0}"),
        ("gitclone",  "https://ghcc.xyz/{0}"),
        ("direct",    "{0}"),
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

    /// <summary>启动下载并应用更新。</summary>
    public static async Task<bool> DownloadAndApplyAsync(Action<double> progress, Action<string>? status)
    {
        if (_downloadUrl is null || _latestVersion is null) return false;

        status?.Invoke("正在测速选择最佳下载源…");
        string fastestUrl = await PickFastestMirror(_downloadUrl);

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
        var assetsDir = Path.Combine(exeDir, "Assets");
        if (!Directory.Exists(assetsDir)) Directory.CreateDirectory(assetsDir);

        var zipPath = Path.Combine(assetsDir, $"SETBoard-v{_latestVersion}.zip");
        var newExePath = Path.Combine(assetsDir, "SETBoard.exe");
        var currentExePath = Environment.ProcessPath ?? Path.Combine(exeDir, "SETBoard.exe");

        status?.Invoke($"正在下载 SETBoard v{_latestVersion}…");

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            using var response = await client.GetAsync(fastestUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;

            long total = response.Content.Headers.ContentLength ?? 0;
            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
            await response.Content.CopyToAsync(fs);

            status?.Invoke("正在解压并替换文件…");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, assetsDir, true);

            // 移动新版本 exe 到根目录
            if (File.Exists(newExePath))
                File.Move(newExePath, currentExePath, true);

            // 清理 zip
            if (File.Exists(zipPath)) File.Delete(zipPath);

            status?.Invoke("更新完成，正在重启…");
            await Task.Delay(500);
            Restart();
            return true;
        }
        catch
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
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

    private static async Task<string> PickFastestMirror(string fileUrl)
    {
        // 首选加速站可用则直接采用，避免额外测速开销
        var (primaryOk, _, primaryUrl) = await TestMirror(Mirrors[0], fileUrl);
        if (primaryOk) return primaryUrl;

        // 首选不可用时，对其余镜像并发测速
        var results = await Task.WhenAll(Mirrors.Skip(1).Select(m => TestMirror(m, fileUrl)));
        var fastest = results.Where(r => r.Success).OrderBy(r => r.DelayMs).FirstOrDefault();
        return fastest.Success ? fastest.Url : primaryUrl;
    }

    private static async Task<(bool Success, long DelayMs, string Url)> TestMirror(
        (string Name, string UrlTemplate) mirror, string fileUrl)
    {
        var url = string.Format(mirror.UrlTemplate, fileUrl);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();
            return (response.IsSuccessStatusCode, sw.ElapsedMilliseconds, url);
        }
        catch
        {
            sw.Stop();
            return (false, long.MaxValue, url);
        }
    }

    private static void Restart()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SETBoard.exe");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
        }
        catch { }
        Environment.Exit(0);
    }

    // ── 公共查询 API（供 UI 调用） ────────────────────
    public static Version? GetLatestVersion() => _latestVersion;
    public static string? GetReleaseNotes() => _releaseNotes;
    public static bool IsChecking() => _checking;
}
