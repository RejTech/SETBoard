using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Runtime.InteropServices;
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
    private readonly Border _inkPanel;
    private readonly StackPanel _inkRow;
    private readonly StackPanel _eraserRow;
    private readonly Button _shapeToggle;
    private readonly Button _passThroughButton;
    private RawPointerInput? _rawInput;
    private bool _passThrough;
    private BoardTool _currentTool = BoardTool.Pen;
    private Window? _barWindow; // 独立工具栏窗口：画布穿透时它仍可交互
    private bool _pptMode;
    private int _slidePage = 1; // 当前幻灯片页码，翻页按钮先按它推算目标页
    private readonly Window _prevNavWindow;
    private readonly Window _nextNavWindow;

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
        // 穿透工具：选中即穿透（笔迹保留、可点下层），选其他工具自动退出
        _passThroughButton = InkUi.CreateIconButton(
            "\uE962", "穿透", () => SetPassThrough(!_passThrough));
        tools.Children.Add(_passThroughButton);
        tools.Children.Add(InkUi.CreateDivider());

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

        _undoButton = InkUi.CreateIconButton("\uE7A7", "撤销", _board.Undo);
        _redoButton = InkUi.CreateIconButton("\uE7A6", "重做", _board.Redo);
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(InkUi.CreateIconButton("\uE74D", "清除", _board.ClearBoard));
        tools.Children.Add(InkUi.CreateIconButton("\uE7E8", "退出", () =>
        {
            if (_pptMode) PptInterop.ExitSlideShow(); // 退出批注时自动结束放映
            Close();
        }));

        // 颜色 / 粗细 / 预览收进二级面板，与白板一致
        _inkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        AddColor(_inkRow, "白色", 0xFFFFFFFF);
        AddColor(_inkRow, "黄色", 0xFFFFD54A);
        AddColor(_inkRow, "红色", 0xFFFF5A5F);
        AddColor(_inkRow, "绿色", 0xFF51CF66);
        AddColor(_inkRow, "蓝色", 0xFF4DABF7);
        AddColor(_inkRow, "黑色", 0xFF111820);

        _inkRow.Children.Add(InkUi.CreateDivider());

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
        _inkRow.Children.Add(widthSlider);

        _widthPreview = new Border
        {
            Width = 28,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromUInt32(_board.InkColor)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 5, 0),
        };
        _inkRow.Children.Add(_widthPreview);

        // 橡皮大小：选中橡皮时替换颜色/粗细行
        _eraserRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, IsVisible = false };
        _eraserRow.Children.Add(new TextBlock
        {
            Text = "橡皮大小",
            Foreground = new SolidColorBrush(Color.Parse("#E8EFF4")),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        });
        var eraserSlider = new Slider
        {
            Minimum = 6,
            Maximum = 48,
            Value = _board.EraserSize,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
        };
        eraserSlider.ValueChanged += (_, e) => _board.SetEraserSize(e.NewValue);
        _eraserRow.Children.Add(eraserSlider);

        _inkPanel = new Border
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
            Child = new StackPanel { Children = { _inkRow, _eraserRow } },
        };

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
                Children = { _shapePanel, _inkPanel, barBorder },
            },
        };

        Content = _board;

        // 工具栏独立窗口：不穿透，画布穿透时仍能选工具、翻页、退出
        _barWindow = new Window
        {
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = _hint,
        };
        UiaTopmost.MakeTopmost(_barWindow);
        _barWindow.SizeChanged += (_, _) => LayoutBarWindow();
        _barWindow.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        // 两角各放一组完整的翻页按钮（上一页 + 下一页），左撇子右撇子都顺手
        _prevNavWindow = CreateNavWindow(CreateNavPair());
        _nextNavWindow = CreateNavWindow(CreateNavPair());
        _prevNavWindow.SizeChanged += (_, _) => LayoutNavWindows();
        _nextNavWindow.SizeChanged += (_, _) => LayoutNavWindows();

        // 与白板一致：点击画布任意处，自动收起颜色/粗细等二级面板
        _board.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (_inkPanel.IsVisible) SetPanelVisible(_inkPanel, false);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);

        // 底部工具栏区域不接收笔迹，避免笔迹从半透明面板下透出
        _hint.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
            {
                LayoutBarWindow();
                UpdateInkExclusions();
            }
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
        // 与白板一致：二级面板默认收起，点工具按钮时展开
        _inkPanel.IsVisible = false;
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

    /// <summary>PPT 模式：锁定缩放和平移，显示两侧翻页按钮。</summary>
    public void SetPptMode(bool enabled)
    {
        _pptMode = enabled;
        _board.ZoomLocked = enabled;
        if (enabled) _board.ResetView(); // 笔迹与幻灯片保持屏幕对齐
        UpdatePageNav();
    }

    /// <summary>桌面批注模式：禁用缩放和平移，保持固定视图。</summary>
    public void SetDesktopAnnotationMode(bool enabled)
    {
        if (enabled)
        {
            _board.ZoomLocked = true;
            _board.ResetView();
        }
    }

    private void UpdatePageNav()
    {
        // 翻页浮钮在屏幕两角，仅 PPT 模式显示；穿透时同样可点（独立窗口不穿透）
        if (_pptMode)
        {
            foreach (var window in new[] { _prevNavWindow, _nextNavWindow })
            {
                if (window.IsVisible) continue;
                window.Opacity = 0; // 淡入出现
                window.Show();
                window.Opacity = 1;
            }
            // Show 后窗口尚未完成测量，须等 Loaded 再定位，否则被系统默认层叠位置覆盖
            Dispatcher.UIThread.Post(LayoutNavWindows, DispatcherPriority.Loaded);
        }
        else
        {
            _prevNavWindow.Hide();
            _nextNavWindow.Hide();
        }
    }

    /// <summary>一组翻页按钮：上一页 + 下一页，两个半透明圆形浮钮。</summary>
    private StackPanel CreateNavPair()
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                CreatePageNavButton("", () => NavigateSlide(-1)),
                CreatePageNavButton("", () => NavigateSlide(1)),
            },
        };
    }

    /// <summary>翻页按钮：先按方向立即切批注页（零等待），再控制 PPT 翻页并立即核对真实页码。</summary>
    private void NavigateSlide(int delta)
    {
        _slidePage = Math.Max(1, _slidePage + delta);
        _board.GoToSlidePage(_slidePage); // 立即切换，不等每秒轮询

        if (delta > 0) PptInterop.NextSlide();
        else PptInterop.PreviousSlide();

        // COM 翻页是同步调用，立即核对；末页 / 首页等边界由这里与 watcher 共同校正
        if (PptInterop.TryGetSlideShow(out int actual)) SyncToSlide(actual);
    }

    private static Window CreateNavWindow(Control content)
    {
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = content,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = TopLevel.OpacityProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() },
            },
        };
        UiaTopmost.MakeTopmost(window);
        return window;
    }

    private void LayoutNavWindows()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.Bounds;
        double scale = screen.Scaling;
        int margin = (int)(20 * scale);
        int height = (int)(_prevNavWindow.Bounds.Height * scale);
        int nextWidth = (int)(_nextNavWindow.Bounds.Width * scale);

        _prevNavWindow.Position = new PixelPoint(area.X + margin, area.Y + area.Height - height - margin);
        _nextNavWindow.Position = new PixelPoint(area.X + area.Width - nextWidth - margin, area.Y + area.Height - height - margin);
    }

    private static Button CreatePageNavButton(string glyph, Action action)
    {
        var button = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.Parse("#991D2A34")), // 半透明圆形
            BorderBrush = new SolidColorBrush(Color.Parse("#2E3D48")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Foreground = new SolidColorBrush(Color.Parse("#EAF3FA")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            // 显隐由所在浮钮窗口控制，按钮自身始终可见
        };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>PPT 模式 / 工具按钮：穿透选中态切换。</summary>
    public void SetPassThrough(bool enabled)
    {
        if (enabled == _passThrough) return;
        if (enabled) EnterPassThrough();
        else ExitPassThrough();
    }

    /// <summary>PPT 模式：批注页跟随幻灯片页码，原页笔迹保留，回页恢复。</summary>
    public void SyncToSlide(int slideNumber)
    {
        if (slideNumber == _slidePage) return;
        _slidePage = slideNumber;
        _board.GoToSlidePage(slideNumber);
    }

    /// <summary>穿透模式：画布不再接收鼠标/触摸，笔迹保留，可点下层内容；工具栏独立窗口不受影响。</summary>
    private void EnterPassThrough()
    {
        _passThrough = true;
        // 穿透时自动收起所有二级菜单，保持工具栏简洁
        SetPanelVisible(_inkPanel, false);
        SetPanelVisible(_shapePanel, false);
        // 与其他工具一样的选中态：穿透高亮，其余工具熄灭
        foreach (var pair in _toolButtons) InkUi.SetActive(pair.Value, false);
        InkUi.SetActive(_passThroughButton, true);
        PassThroughNative.Set(this, true);
    }

    private void ExitPassThrough()
    {
        _passThrough = false;
        InkUi.SetActive(_passThroughButton, false);
        PassThroughNative.Set(this, false);
        // 恢复当前工具的选中态
        foreach (var pair in _toolButtons) InkUi.SetActive(pair.Value, pair.Key == _currentTool);
    }

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
        if (_passThrough) ExitPassThrough(); // 选择其他工具即退出穿透
        _currentTool = tool;
        _board.SetTool(tool);
        foreach (var pair in _toolButtons)
        {
            InkUi.SetActive(pair.Value, pair.Key == tool);
        }

        // 形状工具选中时高亮图形入口，形状行收起后也能看出当前模式
        bool isShape = false;
        foreach (var (shapeTool, _, _) in ShapeItems) isShape |= shapeTool == tool;
        InkUi.SetActive(_shapeToggle, isShape);

        // 二级面板：书写等工具显示颜色/粗细，橡皮显示大小，选择收起
        bool isEraser = tool == BoardTool.Eraser;
        bool wantsInk = !isEraser && tool != BoardTool.Select;
        _inkRow.IsVisible = wantsInk;
        _eraserRow.IsVisible = isEraser;
        SetPanelVisible(_inkPanel, wantsInk || isEraser);
        if (!wantsInk) SetPanelVisible(_shapePanel, false);
    }

    private readonly Dictionary<Control, int> _panelTokens = new();

    /// <summary>二级面板显隐：与白板一致的 190ms 淡入淡出 + 上浮动画。</summary>
    private void SetPanelVisible(Control panel, bool show)
    {
        int token = _panelTokens.TryGetValue(panel, out int last) ? last + 1 : 1;
        _panelTokens[panel] = token;

        panel.Transitions ??= new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(190), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(190), Easing = new CubicEaseOut() },
        };

        if (show)
        {
            panel.RenderTransform = TransformOperations.Parse("translateY(8px)");
            panel.Opacity = 0;
            panel.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_panelTokens[panel] != token) return;
                panel.Opacity = 1;
                panel.RenderTransform = TransformOperations.Parse("translateY(0px)");
            }, DispatcherPriority.Render);
            return;
        }

        if (!panel.IsVisible) return;
        panel.Opacity = 0;
        panel.RenderTransform = TransformOperations.Parse("translateY(6px)");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_panelTokens[panel] == token) panel.IsVisible = false;
        };
        timer.Start();
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

    /// <summary>把工具栏窗口的屏幕矩形登记为笔迹禁区，两侧空白仍可书写。</summary>
    private void UpdateInkExclusions()
    {
        if (_barWindow is null || !_barWindow.IsVisible || _barWindow.ClientSize.Width < 1)
        {
            _board.SetInkExclusions(null);
            return;
        }

        // 工具栏在独立窗口：屏幕像素坐标 → 本窗口 DIP → 画布坐标
        double scale = RenderScaling;
        if (scale < 0.01) scale = 1;
        var topLeft = new Point(
            (_barWindow.Position.X - Position.X) / scale,
            (_barWindow.Position.Y - Position.Y) / scale);
        var local = this.TranslatePoint(topLeft, _board);
        if (local is null) return;

        var size = _barWindow.ClientSize;
        _board.SetInkExclusions(new[] { new Rect(local.Value.X, local.Value.Y, size.Width, size.Height) });
    }

    /// <summary>工具栏窗口在屏幕底部居中（尺寸随二级面板展开变化）。</summary>
    private void LayoutBarWindow()
    {
        if (_barWindow is null) return;
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        // 用窗口自身缩放与实测尺寸，避免多屏 / DPI 下定位偏移
        double scale = _barWindow.RenderScaling;
        if (scale < 0.01) scale = 1;
        var area = screen.Bounds;
        _barWindow.Position = new PixelPoint(
            area.X + (area.Width - (int)(_barWindow.ClientSize.Width * scale)) / 2,
            area.Y + area.Height - (int)(_barWindow.ClientSize.Height * scale));
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
        _barWindow?.Show();
        Dispatcher.UIThread.Post(() =>
        {
            LayoutBarWindow();
            UpdateInkExclusions();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        _barWindow?.Close();
        _barWindow = null;
        _prevNavWindow.Close();
        _nextNavWindow.Close();
        _rawInput?.Dispose();
        _rawInput = null;
        base.OnClosed(e);
    }

    private void OnRawPointerSample(RawPointerSample sample)
    {
        if (_passThrough) return; // 穿透时不产生笔迹

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

/// <summary>Win32 扩展样式：WS_EX_TRANSPARENT 让窗口跳过命中测试，实现点击穿透。</summary>
internal static class PassThroughNative
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    public static void Set(Window window, bool enabled)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;

        int style = GetWindowLong(handle.Handle, GWL_EXSTYLE);
        // LAYERED 保留（透明窗口本身需要）；仅切换 TRANSPARENT 位
        int next = enabled
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(handle.Handle, GWL_EXSTYLE, next);
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
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(110), Easing = new CubicEaseOut() },
            },
            RenderTransformOrigin = RelativePoint.Center,
        };
        // 不设置 ToolTip：悬停提示会干扰触屏书写
        button.PointerEntered += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#33424E"));
        button.PointerExited += (_, _) =>
        {
            button.Background = button.Classes.Contains("active") ? ActiveBrush : Brushes.Transparent;
            button.RenderTransform = TransformOperations.Parse("scale(1)");
        };
        // 按压回弹：与白板按钮一致的触控反馈
        button.PointerPressed += (_, _) => button.RenderTransform = TransformOperations.Parse("scale(0.92)");
        button.PointerReleased += (_, _) => button.RenderTransform = TransformOperations.Parse("scale(1)");
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
