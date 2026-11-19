using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SETBoard;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 快捷方式带 --float 启动时直接进入桌面浮窗，不显示主界面
            bool floating = (desktop.Args ?? Array.Empty<string>()).Any(arg =>
                arg.Equals("--float", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--mini", StringComparison.OrdinalIgnoreCase));

            // 已有实例在运行：正常启动则唤醒其白板主界面，浮窗启动则忽略本次
            if (!SingleInstance.TryAcquire())
            {
                if (!floating) SingleInstance.NotifyRestore();
                desktop.Shutdown();
            }
            else
            {
                var window = new MainWindow();
                if (floating)
                {
                    window.StartInFloatingMode();
                }
                else
                {
                    desktop.MainWindow = window;
                }

                // 后续实例请求恢复时，显示已有窗口的白板主界面
                SingleInstance.StartListener(window.RestoreFromExternal);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
