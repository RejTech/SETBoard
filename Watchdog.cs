using System.Diagnostics;
using Avalonia.Threading;

namespace SETBoard;

/// <summary>
/// 进程守护：主进程与守护子进程互相监视。
/// 主进程被结束且非主动退出时，守护进程自动重新拉起；
/// 主动退出（退出菜单 / 自动更新）通过命名事件通知守护进程一起退出。
/// </summary>
internal static class Watchdog
{
    private const string AllowExitEventName = "SETBoard.AllowExit";
    private const string WatchdogArg = "--watchdog";

    private static Process? _child;
    private static EventWaitHandle? _allowExitEvent;

    /// <summary>是否为守护进程启动参数。</summary>
    public static bool IsWatchdogArgs(string[] args, out int parentPid)
    {
        parentPid = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(WatchdogArg, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out parentPid))
                return true;
        }
        return false;
    }

    /// <summary>主进程调用：启动守护进程，并周期检查其存活。</summary>
    public static void Start()
    {
        _allowExitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, AllowExitEventName);
        // 上次主动退出可能留下信号，启动时必须复位，否则守护进程会立即退出
        _allowExitEvent.Reset();

        EnsureChild();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => EnsureChild();
        timer.Start();
    }

    /// <summary>主动退出前调用：通知守护进程不要重启。</summary>
    public static void SignalAllowExit()
    {
        try { _allowExitEvent?.Set(); } catch { }
    }

    private static void EnsureChild()
    {
        try
        {
            if (_child is { HasExited: false }) return;

            var exe = Environment.ProcessPath;
            if (exe is null) return;

            _child = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{WatchdogArg} {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // 守护进程启动失败不影响主程序运行，下个周期再试
        }
    }

    /// <summary>守护进程入口：阻塞监视主进程，非主动退出时自动重启。</summary>
    public static void Run(int parentPid)
    {
        using var allowExit = new EventWaitHandle(false, EventResetMode.ManualReset, AllowExitEventName);
        var exe = Environment.ProcessPath;

        while (true)
        {
            // 等待主进程退出
            while (true)
            {
                if (allowExit.WaitOne(0)) return;

                bool alive;
                try
                {
                    using var p = Process.GetProcessById(parentPid);
                    alive = !p.HasExited;
                }
                catch
                {
                    alive = false;
                }

                if (!alive) break;
                Thread.Sleep(1500);
            }

            if (allowExit.WaitOne(0)) return;

            // 主进程被结束且未允许退出 → 重新拉起并继续守护新进程
            try
            {
                if (exe is null) return;
                using var restarted = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                });
                if (restarted is null) return;
                parentPid = restarted.Id;
            }
            catch
            {
                Thread.Sleep(3000);
            }
        }
    }
}
