using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using SETBoard.Board;

namespace SETBoard;

/// <summary>桌面上常驻的小浮窗：可进入桌面批注、返回白板或退出。</summary>
internal sealed class DesktopInkBar : Window
{
    public event EventHandler? AnnotateRequested;
    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public DesktopInkBar()
    {
        Title = "SETBoard 浮窗";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.Add(InkUi.CreateButton("批注", () => AnnotateRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(InkUi.CreateButton("白板", () => RestoreRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(InkUi.CreateButton("退出", () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21D2A34")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(5),
            BoxShadow = BoxShadows.Parse("0 10 26 0 #66000000"),
            Child = panel,
        };

        WindowFx.FadeIn(this);
    }

    /// <summary>显示在主屏右下角（避开任务栏）。</summary>
    public void ShowAtDesktopCorner()
    {
        Show();
        Avalonia.Threading.Dispatcher.UIThread.Post(PlaceAtCorner, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void PlaceAtCorner()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        double scale = screen.Scaling;
        double width = Bounds.Width > 1 ? Bounds.Width : DesiredSize.Width;
        double height = Bounds.Height > 1 ? Bounds.Height : DesiredSize.Height;
        if (width < 1 || height < 1) return;

        var area = screen.WorkingArea;
        Position = new PixelPoint(
            area.X + area.Width - (int)(width * scale) - (int)(24 * scale),
            area.Y + area.Height - (int)(height * scale) - (int)(20 * scale));
    }
}

/// <summary>全屏透明的桌面批注层：直接在桌面与其他窗口之上书写。</summary>
internal sealed class DesktopAnnotationWindow : Window
{
    private readonly BoardCanvas _board;

    public DesktopAnnotationWindow()
    {
        Title = "桌面批注";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _board = new BoardCanvas();
        _board.SetDocument(new BoardDocument { Background = 0x00000000, ShowGrid = false });
        _board.SetInkColor(0xFFFF5A5F);
        _board.SetInkWidth(4);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actions.Children.Add(InkUi.CreateButton("清除", () => _board.ClearBoard()));
        actions.Children.Add(InkUi.CreateButton("退出批注", Close));

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21D2A34")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(5),
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = BoxShadows.Parse("0 8 22 0 #59000000"),
            Child = actions,
        };

        Content = new Panel
        {
            Children = { _board, hint },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        WindowFx.FadeIn(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.Bounds;
        Position = new PixelPoint(area.X, area.Y);
        Width = area.Width / screen.Scaling;
        Height = area.Height / screen.Scaling;
    }
}

internal static class WindowFx
{
    /// <summary>窗口显示时淡入，避免浮层生硬地“弹”出来。</summary>
    public static void FadeIn(Window window, int milliseconds = 200)
    {
        window.Opened += async (_, _) =>
        {
            var fade = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters = { new Setter(Visual.OpacityProperty, 0d) },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters = { new Setter(Visual.OpacityProperty, 1d) },
                    },
                },
            };
            window.Opacity = 0;
            try
            {
                await fade.RunAsync(window);
            }
            finally
            {
                window.Opacity = 1; // 兜底，避免动画被打断后停在半透明
            }
        };
    }
}

internal static class InkUi
{
    public static Button CreateButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 13,
            Padding = new Thickness(16, 9),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Foreground = new SolidColorBrush(Color.Parse("#D5DFE6")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => action();
        button.PointerEntered += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#33424E"));
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }
}
