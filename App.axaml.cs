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
            var window = new MainWindow();

            // 快捷方式带 --float 启动时直接进入桌面浮窗，不显示主界面
            bool floating = (desktop.Args ?? Array.Empty<string>()).Any(arg =>
                arg.Equals("--float", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--mini", StringComparison.OrdinalIgnoreCase));

            if (floating)
            {
                window.StartInFloatingMode();
            }
            else
            {
                desktop.MainWindow = window;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
