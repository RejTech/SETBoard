using Avalonia;

namespace SETBoard;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 守护进程：不加载 Avalonia，只监视主进程
        if (Watchdog.IsWatchdogArgs(args, out int parentPid))
        {
            Watchdog.Run(parentPid);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
