using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SETBoard;

/// <summary>
/// 更新提示对话框：非阻塞式，显示在右下角，允许用户选择立即更新或稍后再说。
/// </summary>
internal sealed class UpdateDialog : Window
{
    private readonly Window _owner;

    public UpdateDialog(Window owner)
    {
        _owner = owner;
        Title = "SETBoard 更新";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.Parse("#0C1B24"));
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        RequestedThemeVariant = ThemeVariant.Dark;

        Content = BuildContent();
        WindowFx.FadeIn(this);
    }

    private Control BuildContent()
    {
        var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 20), Spacing = 10 };

        // 标题行
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var iconBorder = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            Background = MainWindow.AppIcon,
        };
        titleRow.Children.Add(iconBorder);
        titleRow.Children.Add(new TextBlock
        {
            Text = "发现新版本",
            FontSize = 18, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F2F6F9")),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(titleRow);

        // 版本信息
        var versionText = new TextBlock
        {
            Text = $"v{AutoUpdater.GetLatestVersion()}",
            FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FFD54A")),
            Margin = new Thickness(0, 2, 0, 4),
        };
        panel.Children.Add(versionText);

        // 更新说明（最多显示 3 行）
        var notes = AutoUpdater.GetReleaseNotes();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            var notesBlock = new TextBlock
            {
                Text = string.Join("\n", notes.Split('\n').Take(3)),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#9FB4C1")),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            };
            panel.Children.Add(notesBlock);
        }

        // 操作按钮
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };

        var laterBtn = new Button
        {
            Content = "稍后再说",
            Height = 38, Padding = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#33424E")),
            Foreground = Brushes.White,
            FontSize = 13,
        };
        laterBtn.Click += (_, _) => Close();

        var updateBtn = new Button
        {
            Content = "立即更新",
            Height = 38, Padding = new Thickness(24, 0, 24, 0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#FFD54A")),
            Foreground = new SolidColorBrush(Color.Parse("#141A10")),
            BorderThickness = new Thickness(0),
            FontSize = 13, FontWeight = FontWeight.SemiBold,
        };

        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };

        var statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FA6B4")),
            Margin = new Thickness(0, 4, 0, 0),
            IsVisible = false,
        };

        updateBtn.Click += async (_, _) =>
        {
            updateBtn.IsEnabled = laterBtn.IsEnabled = false;
            progressBar.IsVisible = statusText.IsVisible = true;

            bool success = await AutoUpdater.DownloadAndApplyAsync(
                p => Dispatcher.UIThread.Post(() => progressBar.Value = p),
                s => Dispatcher.UIThread.Post(() => statusText.Text = s));

            if (!success)
            {
                statusText.Text = "更新失败，请手动从 GitHub 下载";
                statusText.Foreground = new SolidColorBrush(Color.Parse("#FF9B9B"));
                updateBtn.IsEnabled = laterBtn.IsEnabled = true;
            }
        };

        actions.Children.Add(laterBtn);
        actions.Children.Add(updateBtn);
        panel.Children.Add(actions);
        panel.Children.Add(progressBar);
        panel.Children.Add(statusText);

        // 30 秒后自动关闭
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => Close();

        return panel;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 定位到主窗口右下角
        var ownerPos = _owner.Position;
        var ownerBounds = _owner.Bounds;
        Position = new PixelPoint(
            (int)(ownerPos.X + ownerBounds.Width - Bounds.Width - 20),
            (int)(ownerPos.Y + ownerBounds.Height - Bounds.Height - 20));
    }
}
