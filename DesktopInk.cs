using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SETBoard.Board;

namespace SETBoard;

/// <summary>桌面上常驻的小浮窗：可进入桌面批注或返回白板。</summary>
internal sealed class DesktopInkBar : Window
{
    public event EventHandler? AnnotateRequested;
    public event EventHandler? RestoreRequested;

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
        panel.Children.Add(InkUi.CreateIconButton("\uE70F", "桌面批注", () => AnnotateRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(InkUi.CreateIconButton("\uE7C3", "返回白板", () => RestoreRequested?.Invoke(this, EventArgs.Empty)));

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21D2A34")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(4),
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
    private readonly Border _hint;
    private readonly Border _shapePanel;
    private readonly Button _shapeToggle;
    private RawPointerInput? _rawInput;

    private static readonly (BoardTool Tool, string Tip, string Data)[] ShapeItems =
    {
        (BoardTool.Line, "直线", "M5 19L19 5"),
        (BoardTool.Arrow, "箭头", "M5 19L19 5 M13.5 5H19v5.5"),
        (BoardTool.Rectangle, "矩形", "M4.5 6.5h15v11h-15z"),
        (BoardTool.Ellipse, "圆形", "M12 4.5a7.5 7.5 0 1 0 0 15a7.5 7.5 0 1 0 0-15"),
        (BoardTool.Triangle, "三角形", "M12 4.5L20 19.5H4z"),
    };

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

        // 形状二级菜单：展开在主行上方
        var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        foreach (var (tool, tip, data) in ShapeItems)
        {
            AddShape(shapeRow, tool, tip, data);
        }

        _shapePanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21D2A34")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false,
            BoxShadow = BoxShadows.Parse("0 8 22 0 #59000000"),
            Child = shapeRow,
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        AddTool(tools, "\uE70F", "画笔", BoardTool.Pen);
        AddTool(tools, "\uE7E6", "荧光笔", BoardTool.Highlighter);
        AddTool(tools, "\uE75C", "橡皮", BoardTool.Eraser);
        AddTool(tools, "\uE8B3", "选择", BoardTool.Select);
        AddTool(tools, "\uE790", "填色", BoardTool.Fill);

        // 图形工具收进二级菜单，与主窗口一致
        _shapeToggle = InkUi.CreateShapeButton(
            "M4.5 6.5h9v9h-9z M15 9.5a4.5 4.5 0 1 0 0 9a4.5 4.5 0 1 0 0-9",
            "图形", ToggleShapePanel);
        tools.Children.Add(_shapeToggle);

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
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(widthSlider, "笔迹粗细");
        widthSlider.ValueChanged += (_, e) =>
        {
            _board.SetInkWidth(e.NewValue);
            UpdateWidthPreview();
        };
        tools.Children.Add(widthSlider);

        _widthPreview = new Border
        {
            Width = 28,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromUInt32(_board.InkColor)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 5, 0),
        };
        tools.Children.Add(_widthPreview);

        tools.Children.Add(InkUi.CreateDivider());

        _undoButton = InkUi.CreateIconButton("\uE7A7", "撤销", _board.Undo);
        _redoButton = InkUi.CreateIconButton("\uE7A6", "重做", _board.Redo);
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(InkUi.CreateIconButton("\uE74D", "清除", _board.ClearBoard));
        tools.Children.Add(InkUi.CreateIconButton("\uE7E8", "退出", Close));

        var barBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21D2A34")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 8 22 0 #59000000"),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                MaxWidth = 1500,
                Content = tools,
            },
        };

        _hint = new Border
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _shapePanel, barBorder },
            },
        };

        Content = new Panel
        {
            Children = { _board, _hint },
        };

        // 底部工具栏区域不接收笔迹，避免笔迹从半透明面板下透出
        _hint.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) UpdateInkExclusions();
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

    private void AddTool(Panel panel, string glyph, string tip, BoardTool tool)
    {
        var button = InkUi.CreateIconButton(glyph, tip, () => SetTool(tool));
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    private void AddShape(Panel panel, BoardTool tool, string tip, string data)
    {
        var button = InkUi.CreateShapeButton(data, tip, () =>
        {
            SetTool(tool);
            SetShapePanelVisible(false);
        });
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    private void ToggleShapePanel() => SetShapePanelVisible(!_shapePanel.IsVisible);

    private void SetShapePanelVisible(bool visible) => _shapePanel.IsVisible = visible;

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

    /// <summary>把底部工具栏自身的矩形登记为笔迹禁区，两侧空白仍可书写。</summary>
    private void UpdateInkExclusions()
    {
        var topLeft = _hint.TranslatePoint(new Point(0, 0), _board);
        if (topLeft is null) return;

        var bounds = _hint.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            _board.SetInkExclusions(null);
            return;
        }

        _board.SetInkExclusions(new[] { new Rect(topLeft.Value.X, topLeft.Value.Y, bounds.Width, bounds.Height) });
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
        Dispatcher.UIThread.Post(UpdateInkExclusions, Avalonia.Threading.DispatcherPriority.Loaded);
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
    private static readonly IBrush IconIdleBrush = new SolidColorBrush(Color.Parse("#D5DFE6"));

    /// <summary>统一外观的图标按钮：固定尺寸 + 悬停/激活反馈 + 提示。</summary>
    private static Button CreateChrome(string tip)
    {
        var button = new Button
        {
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Foreground = IconIdleBrush,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Transitions = new Transitions
            {
                new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(130) },
                new BrushTransition { Property = Button.ForegroundProperty, Duration = TimeSpan.FromMilliseconds(130) },
            },
        };
        ToolTip.SetTip(button, tip);
        button.PointerEntered += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#33424E"));
        button.PointerExited += (_, _) =>
            button.Background = button.Classes.Contains("active") ? ActiveBrush : Brushes.Transparent;
        return button;
    }

    /// <summary>Fluent 字形图标按钮。</summary>
    public static Button CreateIconButton(string glyph, string tip, Action action)
    {
        var button = CreateChrome(tip);
        button.Content = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>矢量图形图标按钮（形状工具）。</summary>
    public static Button CreateShapeButton(string data, string tip, Action action)
    {
        var button = CreateChrome(tip);
        button.Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Stroke = new SolidColorBrush(Color.Parse("#C3D1DB")),
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
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
        var foreground = active ? Brushes.White : IconIdleBrush;
        button.Foreground = foreground;
        if (button.Content is Avalonia.Controls.Shapes.Path path)
        {
            path.Stroke = foreground;
        }
    }
}
