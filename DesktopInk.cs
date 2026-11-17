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

        UiaTopmost.MakeTopmost(this);
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
    private readonly Dictionary<BoardTool, Button> _toolButtons = new();
    private readonly List<Button> _colorButtons = new();
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Border _widthPreview;
    private RawPointerInput? _rawInput;

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
        _board.SetDocument(new BoardDocument { Background = 0x01000000, ShowGrid = false });
        _board.SetInkColor(0xFFFF5A5F);
        _board.SetInkWidth(4);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        AddTool(tools, "画笔", BoardTool.Pen);
        AddTool(tools, "荧光", BoardTool.Highlighter);
        AddTool(tools, "橡皮", BoardTool.Eraser);
        AddTool(tools, "选择", BoardTool.Select);
        AddTool(tools, "填色", BoardTool.Fill);
        AddTool(tools, "直线", BoardTool.Line);
        AddTool(tools, "箭头", BoardTool.Arrow);
        AddTool(tools, "矩形", BoardTool.Rectangle);
        AddTool(tools, "圆形", BoardTool.Ellipse);
        AddTool(tools, "三角", BoardTool.Triangle);

        tools.Children.Add(InkUi.CreateDivider());

        AddColor(tools, "白色", 0xFFFFFFFF);
        AddColor(tools, "黄色", 0xFFFFD54A);
        AddColor(tools, "红色", 0xFFFF5A5F);
        AddColor(tools, "绿色", 0xFF51CF66);
        AddColor(tools, "蓝色", 0xFF4DABF7);
        AddColor(tools, "黑色", 0xFF111820);

        tools.Children.Add(InkUi.CreateDivider());

        var widthSlider = new Slider
        {
            Minimum = 1,
            Maximum = 16,
            Value = _board.InkWidth,
            Width = 108,
            VerticalAlignment = VerticalAlignment.Center,
        };
        widthSlider.ValueChanged += (_, e) =>
        {
            _board.SetInkWidth(e.NewValue);
            UpdateWidthPreview();
        };
        tools.Children.Add(widthSlider);

        _widthPreview = new Border
        {
            Width = 34,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromUInt32(_board.InkColor)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 5, 0),
        };
        tools.Children.Add(_widthPreview);

        tools.Children.Add(InkUi.CreateDivider());

        _undoButton = InkUi.CreateButton("撤销", _board.Undo);
        _redoButton = InkUi.CreateButton("重做", _board.Redo);
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(InkUi.CreateButton("清除", _board.ClearBoard));
        tools.Children.Add(InkUi.CreateButton("退出", Close));

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21D2A34")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            BoxShadow = BoxShadows.Parse("0 8 22 0 #59000000"),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                MaxWidth = 1500,
                Content = tools,
            },
        };

        Content = new Panel
        {
            Children = { _board, hint },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z)
            {
                _board.Undo();
                e.Handled = true;
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y)
            {
                _board.Redo();
                e.Handled = true;
            }
        };

        _board.History.Changed += (_, _) => UpdateHistoryButtons();
        SetTool(BoardTool.Pen);
        UpdateColorButtons();
        UpdateWidthPreview();
        UpdateHistoryButtons();

        UiaTopmost.MakeTopmost(this);
        WindowFx.FadeIn(this);
    }

    private void AddTool(Panel panel, string label, BoardTool tool)
    {
        var button = InkUi.CreateButton(label, () => SetTool(tool));
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    private void AddColor(Panel panel, string name, uint color)
    {
        var button = InkUi.CreateColorButton(name, color, () =>
        {
            _board.SetInkColor(color);
            UpdateColorButtons();
            UpdateWidthPreview();
        });
        _colorButtons.Add(button);
        panel.Children.Add(button);
    }

    private void SetTool(BoardTool tool)
    {
        _board.SetTool(tool);
        foreach (var pair in _toolButtons)
        {
            InkUi.SetActive(pair.Value, pair.Key == tool);
        }
    }

    private void UpdateColorButtons()
    {
        foreach (var button in _colorButtons)
        {
            InkUi.SetActive(button, button.Tag is uint color && color == _board.InkColor);
        }
    }

    private void UpdateWidthPreview()
    {
        _widthPreview.Height = Math.Clamp(_board.InkWidth, 2, 16);
        _widthPreview.CornerRadius = new CornerRadius(_widthPreview.Height / 2);
        _widthPreview.Background = new SolidColorBrush(Color.FromUInt32(_board.InkColor));
    }

    private void UpdateHistoryButtons()
    {
        _undoButton.IsEnabled = _board.History.CanUndo;
        _redoButton.IsEnabled = _board.History.CanRedo;
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
        _rawInput ??= RawPointerInput.Attach(this, OnRawPointerSample);
    }

    protected override void OnClosed(EventArgs e)
    {
        _rawInput?.Dispose();
        _rawInput = null;
        base.OnClosed(e);
    }

    private void OnRawPointerSample(RawPointerSample sample)
    {
        var local = this.TranslatePoint(sample.Position, _board);
        if (local is null) return;

        switch (sample.Kind)
        {
            case RawPointerKind.Down:
                _board.RawPointerDown(sample.ContactId, local.Value, sample.Pressure, sample.IsPen);
                break;
            case RawPointerKind.Move:
                _board.RawPointerMove(sample.ContactId, local.Value, sample.Pressure);
                break;
            case RawPointerKind.Up:
                _board.RawPointerUp(sample.ContactId, sample.IsPen);
                break;
        }
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
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#425462"));

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
            Transitions = new Transitions
            {
                new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(130) },
                new BrushTransition { Property = Button.ForegroundProperty, Duration = TimeSpan.FromMilliseconds(130) },
            },
        };
        button.Click += (_, _) => action();
        button.PointerEntered += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#33424E"));
        button.PointerExited += (_, _) =>
            button.Background = button.Classes.Contains("active") ? ActiveBrush : Brushes.Transparent;
        return button;
    }

    public static Button CreateColorButton(string name, uint color, Action action)
    {
        var button = new Button
        {
            Tag = color,
            Width = 34,
            Height = 34,
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Cursor = new Cursor(StandardCursorType.Hand),
            Transitions = new Transitions
            {
                new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(130) },
            },
            Content = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromUInt32(color)),
                BorderBrush = new SolidColorBrush(Color.Parse("#55FFFFFF")),
                BorderThickness = new Thickness(1),
            },
        };
        button.Click += (_, _) => action();
        button.PointerEntered += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#33424E"));
        button.PointerExited += (_, _) =>
            button.Background = button.Classes.Contains("active") ? ActiveBrush : Brushes.Transparent;
        return button;
    }

    public static Border CreateDivider() => new()
    {
        Width = 1,
        Height = 24,
        Margin = new Thickness(5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse("#40505C")),
    };

    public static void SetActive(Button button, bool active)
    {
        button.Classes.Set("active", active);
        button.Background = active ? ActiveBrush : Brushes.Transparent;
        button.Foreground = active ? Brushes.White : new SolidColorBrush(Color.Parse("#D5DFE6"));
    }
}
