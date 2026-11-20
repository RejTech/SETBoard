using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace SETBoard;

/// <summary>
/// 通过 COM 读取 PowerPoint / WPS 演示的放映状态。
/// 使用 GetActiveObject 连接已在运行的实例，不会启动新进程。
/// </summary>
internal static class PptInterop
{
    // PowerPoint、WPS 演示（旧版 Kwpp / 新版 Wpp），接口均兼容 Office 对象模型
    private static readonly string[] ProgIds =
    {
        "PowerPoint.Application",
        "Kwpp.Application",
        "Wpp.Application",
    };

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);

    /// <summary>获取当前放映视图的 COM 对象；未放映时返回 false。</summary>
    private static bool TryGetView(out object? view)
    {
        view = null;

        foreach (var progId in ProgIds)
        {
            try
            {
                if (CLSIDFromProgID(progId, out var clsid) != 0) continue;
                if (GetActiveObject(ref clsid, IntPtr.Zero, out var com) != 0 || com is null) continue;

                dynamic app = com;
                dynamic shows = app.SlideShowWindows;
                if ((int)shows.Count == 0) continue; // 已打开但未放映

                view = shows.Item(1).View;
                return true;
            }
            catch
            {
                // COM 不可用 / 应用正在退出 / 接口不兼容：尝试下一个
            }
        }

        return false;
    }

    /// <summary>检测到正在放映时返回 true，并给出当前幻灯片页码（1 基）。</summary>
    public static bool TryGetSlideShow(out int slideIndex)
    {
        slideIndex = 0;
        if (!TryGetView(out var view)) return false;

        try
        {
            dynamic v = view!;
            slideIndex = (int)v.Slide.SlideIndex;
            return slideIndex > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>控制放映：下一页。</summary>
    public static void NextSlide() => InvokeView(v => v.Next());

    /// <summary>控制放映：上一页。</summary>
    public static void PreviousSlide() => InvokeView(v => v.Previous());

    /// <summary>结束当前放映。</summary>
    public static void ExitSlideShow() => InvokeView(v => v.Exit());

    private static void InvokeView(Action<dynamic> action)
    {
        if (!TryGetView(out var view)) return;
        try
        {
            action(view!);
        }
        catch
        {
            // 放映恰好结束或应用退出时忽略
        }
    }
}

/// <summary>1 秒轮询放映状态，产生「开始放映 / 翻页 / 结束放映」事件（UI 线程）。</summary>
internal sealed class PptWatcher : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private bool _inShow;
    private int _slide;

    /// <summary>进入放映，参数为当前幻灯片页码。</summary>
    public event EventHandler<int>? SlideShowStarted;

    /// <summary>放映中翻页，参数为新的幻灯片页码。</summary>
    public event EventHandler<int>? SlideChanged;

    /// <summary>放映结束。</summary>
    public event EventHandler? SlideShowEnded;

    public PptWatcher()
    {
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        bool inShow = PptInterop.TryGetSlideShow(out int slide);

        if (inShow)
        {
            if (!_inShow)
            {
                _inShow = true;
                _slide = slide;
                SlideShowStarted?.Invoke(this, slide);
            }
            else if (slide != _slide)
            {
                _slide = slide;
                SlideChanged?.Invoke(this, slide);
            }
        }
        else if (_inShow)
        {
            _inShow = false;
            SlideShowEnded?.Invoke(this, EventArgs.Empty);
        }
    }
}
