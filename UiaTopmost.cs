using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SETBoard;

/// <summary>
/// 通过 Win32 强制窗口停留在 TOPMOST 组顶部：
/// 显示、激活、失焦时立即重设，并周期性复查，
/// 避免被后来打开的置顶窗口或全屏教学软件盖住。
/// </summary>
internal static class UiaTopmost
{
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly List<WeakReference<Window>> Tracked = new();
    private static DispatcherTimer? _timer;

    /// <summary>注册窗口：之后会持续保持在最前。</summary>
    public static void MakeTopmost(Window window)
    {
        Prune();
        if (!Tracked.Any(w => w.TryGetTarget(out var existing) && ReferenceEquals(existing, window)))
        {
            Tracked.Add(new WeakReference<Window>(window));
        }

        window.Opened += (_, _) => Apply(window);
        window.Activated += (_, _) => Apply(window);
        window.Deactivated += (_, _) => Apply(window);
        window.Closed += (_, _) => Prune();

        Apply(window);
        EnsureTimer();
    }

    private static void Apply(Window window)
    {
        if (!window.IsVisible) return;

        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;

        // 直接置为 TOPMOST 组顶部，不改位置尺寸、不抢焦点
        SetWindowPos(handle.Handle, HwndTopmost, 0, 0, 0, 0,
            (uint)(SwpNoMove | SwpNoSize | SwpNoActivate));
    }

    /// <summary>周期性重设，防止后来的置顶窗口抢占 Z 序。</summary>
    private static void EnsureTimer()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) =>
        {
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                if (!Tracked[i].TryGetTarget(out var window))
                {
                    Tracked.RemoveAt(i);
                    continue;
                }

                Apply(window);
            }
        };
        _timer.Start();
    }

    private static void Prune() => Tracked.RemoveAll(w => !w.TryGetTarget(out _));
}
