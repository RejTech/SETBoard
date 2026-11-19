using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SETBoard.Board;

namespace SETBoard;

public partial class MainWindow : Window
{
    /// <summary>软件图标，所有弹窗共用。</summary>
    internal static readonly ImageBrush AppIcon = CreateAppIcon();

    private static ImageBrush CreateAppIcon()
    {
        try
        {
            var bitmap = new Bitmap(@"f:\SwiftEducationTechnology\SETBoard\Assets\icon.png");
            return new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
        }
        catch
        {
            return new ImageBrush { Stretch = Stretch.Uniform };
        }
    }

    /// <summary>统一弹窗视觉语言：标题图标 + 圆角面板 + 柔和阴影。</summary>
    private static void SetupDialog(Window dialog, string title)
    {
        dialog.Title = title;
        dialog.Width = 420;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.CanResize = false;
        dialog.ShowInTaskbar = false;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.Background = new SolidColorBrush(Color.Parse("#0C1B24"));
        dialog.FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        dialog.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        // 窗口左上角图标（通过 Tag 暂存）
        dialog.Tag = title;
    }

    /// <summary>创建统一风格的弹窗内容面板。</summary>
    private static StackPanel CreateDialogPanel(params Control[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(28, 18, 28, 22), Spacing = 10 };
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    /// <summary>创建统一风格的弹窗按钮。</summary>
    private static Button CreateDialogButton(string text, string bgColor = "#33424E", string textColor = "#FFFFFF") => new()
    {
        Content = text,
        Padding = new Thickness(20, 8, 20, 8),
        CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Color.Parse(bgColor)),
        Foreground = new SolidColorBrush(Color.Parse(textColor)),
        FontSize = 13,
    };

    private readonly Button[] _toolButtons;
    private readonly Button[] _swatches;

    private DispatcherTimer? _toastTimer;
    private int _toastToken;
    private bool _dirty;
    private BoardTool _lastShape = BoardTool.Rectangle;
    private BoardTool _tool = BoardTool.Pen;
    private bool _inkOptionsOpen;
    private int _inkOptionsToken;
    private bool _pageAnimating;

    public MainWindow()
    {
        InitializeComponent();

        _toolButtons = new[]
        {
            PanButton, PenButton, HighlighterButton, EraserButton, SelectButton, FillButton,
        };
        _swatches = new[]
        {
            WhiteSwatch, YellowSwatch, RedSwatch, GreenSwatch, BlueSwatch, GraySwatch,
            OrangeSwatch, PurpleSwatch, CyanSwatch, BlackSwatch, CustomSwatch,
        };

        Board.ViewChanged += (_, _) => UpdateZoomLabel();
        Board.DocumentChanged += (_, _) => OnDocumentChanged();
        Board.PageChanged += (_, _) => OnPageChanged();
        Board.History.Changed += (_, _) => UpdateHistoryButtons();
        Board.DoubleClicked += OnBoardDoubleClicked;

        UpdateZoomLabel();
        UpdateHistoryButtons();
        UpdatePageLabel();
        UpdateGridMenu();
        SetTool(BoardTool.Pen);
        Toast.IsVisible = false;
        ClearPageSlider.AddHandler(PointerPressedEvent, OnClearSlidePressed, RoutingStrategies.Tunnel, true);
        ClearPageSlider.AddHandler(PointerReleasedEvent, OnClearSlideReleased, RoutingStrategies.Bubble, true);

        // 点击工具条以外的地方（画布、其他按钮、菜单等）收起二级选项菜单
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, true);

        // 底栏悬浮面板区域不接收书写：布局变化时重算笔迹禁区
        ToolDock.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) UpdateInkExclusions();
        };
        SizeChanged += (_, _) => UpdateInkExclusions();

        // 触屏/触控笔直读：落笔与轨迹不再等 Avalonia 的指针事件排队
        Opened += (_, _) =>
        {
            _rawInput ??= RawPointerInput.Attach(this, OnRawPointerSample);
            Board.RenderTransform = TransformOperations.Parse("scale(0.985)");
            Dispatcher.UIThread.Post(() =>
            {
                Board.Opacity = 1;
                Board.RenderTransform = TransformOperations.Parse("scale(1)");
            }, DispatcherPriority.Render);

            // 启动后延迟检测更新（避免阻塞主界面渲染）
            Dispatcher.UIThread.Post(async () =>
            {
                UpdateInkExclusions();
                await Task.Delay(2000);
                await AutoUpdater.CheckAsync(update =>
                {
                    if (update) new UpdateDialog(this).Show(this);
                });
            }, DispatcherPriority.Background);
        };
        Closed += (_, _) =>
        {
            _rawInput?.Dispose();
            _rawInput = null;
        };
    }

    private RawPointerInput? _rawInput;

    /// <summary>把 Win32 直读的窗口坐标换算到画布坐标，交给画布立即开笔/续笔。</summary>
    private void OnRawPointerSample(RawPointerSample sample)
    {
        if (Board is null) return;
        var local = this.TranslatePoint(sample.Position, Board);
        if (local is null) return;

        switch (sample.Kind)
        {
            case RawPointerKind.Down:
                Board.RawPointerDown(sample.ContactId, local.Value, sample.Pressure, sample.IsPen);
                break;
            case RawPointerKind.Move:
                Board.RawPointerMove(sample.ContactId, local.Value, sample.Pressure);
                break;
            case RawPointerKind.Up:
                Board.RawPointerUp(sample.ContactId, sample.IsPen);
                break;
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_inkOptionsOpen && !EraserOptions.IsVisible) return;
        if (e.Source is Visual source && IsInside(source, ToolDock)) return;
        SetInkOptionsVisible(false);
        EraserOptions.IsVisible = false;
    }

    /// <summary>把底栏悬浮面板占用的屏幕区域登记为笔迹禁区，笔迹不会从半透明面板下透出。</summary>
    private void UpdateInkExclusions()
    {
        if (Board is null || ToolDock is null) return;

        var topLeft = ToolDock.TranslatePoint(new Point(0, 0), Board);
        if (topLeft is null) return;

        double top = topLeft.Value.Y;
        double height = Board.Bounds.Height;
        if (top <= 0 || top >= height)
        {
            Board.SetInkExclusions(null);
            return;
        }

        Board.SetInkExclusions(new[] { new Rect(0, top, Board.Bounds.Width, height - top) });
    }

    /// <summary>展开/收起二级选项菜单，带淡入淡出与上浮动画。</summary>
    private void SetInkOptionsVisible(bool show)
    {
        int token = ++_inkOptionsToken;
        _inkOptionsOpen = show;

        if (show)
        {
            InkOptions.RenderTransform = TransformOperations.Parse("translateY(10px)");
            InkOptions.Opacity = 0;
            InkOptions.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (token != _inkOptionsToken) return;
                InkOptions.Opacity = 1;
                InkOptions.RenderTransform = TransformOperations.Parse("translateY(0px)");
            }, DispatcherPriority.Render);
            return;
        }

        if (!InkOptions.IsVisible) return;
        InkOptions.Opacity = 0;
        InkOptions.RenderTransform = TransformOperations.Parse("translateY(6px)");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token == _inkOptionsToken) InkOptions.IsVisible = false;
        };
        timer.Start();
    }

    private static bool IsInside(Visual source, Visual container)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, container)) return true;
        }

        return false;
    }

    private void OnDocumentChanged()
    {
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = _dirty ? "SETBoard 白板 *" : "SETBoard 白板";
    }

    private void UpdateZoomLabel()
    {
        ZoomText.Text = $"{Board.Zoom * 100:0}%";
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = Board.History.CanUndo;
        RedoButton.IsEnabled = Board.History.CanRedo;
    }

    private void UpdatePageLabel()
    {
        PageText.Text = $"{Board.PageNumber}/{Board.PageCount}";
        PrevPageButton.IsEnabled = Board.PageNumber > 1;
        NextPageButton.IsEnabled = Board.PageNumber < Board.PageCount;
        DeletePageMenuItem.IsEnabled = Board.PageCount > 1;
    }

    private void UpdateGridMenu()
    {
        GridMenuItem.IsChecked = Board.Document.ShowGrid;
    }

    private void OnPageChanged()
    {
        UpdatePageLabel();
        UpdateHistoryButtons();
    }

    private void SetTool(BoardTool tool)
    {
        Board.SetTool(tool);
        _tool = tool;
        bool isShape = IsShapeTool(tool);
        if (isShape) _lastShape = tool;

        foreach (var button in _toolButtons)
        {
            bool active = button.Tag is string tag
                          && Enum.TryParse<BoardTool>(tag, out var value)
                          && value == tool;
            button.Classes.Set("active", active);
        }

        foreach (var button in ShapePicker.Children.OfType<Button>())
        {
            button.Classes.Set("active", button.Tag is string tag
                                         && Enum.TryParse<BoardTool>(tag, out var value)
                                         && value == tool);
        }

        ShapePicker.IsVisible = isShape;
        SolidPicker.IsVisible = false;
        SetInkOptionsVisible(false);
        EraserOptions.IsVisible = false;
        // 填色要选颜色，直接把色板露出来
        if (tool == BoardTool.Fill) SetInkOptionsVisible(true);
        UpdateInkPreview();
    }

    private static bool IsShapeTool(BoardTool tool) => tool is BoardTool.Line or BoardTool.Arrow
        or BoardTool.Rectangle or BoardTool.Ellipse or BoardTool.Triangle;

    /// <summary>当前工具是否有二级选项（颜色 / 粗细 / 形状）。</summary>
    private static bool HasInkOptions(BoardTool tool) =>
        tool is BoardTool.Pen or BoardTool.Highlighter or BoardTool.Fill || IsShapeTool(tool);

    /// <summary>再次点击已选中的工具时，展开或收起二级选项菜单。</summary>
    private void ToggleInkOptions()
    {
        if (_tool == BoardTool.Eraser)
        {
            EraserOptions.IsVisible = !EraserOptions.IsVisible;
            ClearPageSlider.Value = 0;
            return;
        }
        if (!HasInkOptions(_tool)) return;
        SetInkOptionsVisible(!_inkOptionsOpen);
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag
            && Enum.TryParse<BoardTool>(tag, out var tool))
        {
            if (tool == _tool)
            {
                ToggleInkOptions();
                return;
            }

            SetTool(tool);
        }
    }

    private void OnShapeButtonClick(object? sender, RoutedEventArgs e)
    {
        CloseInsertFlyout();
        SolidPicker.IsVisible = false;
        if (IsShapeTool(_tool))
        {
            ToggleInkOptions();
            return;
        }

        SetTool(_lastShape);
        SetInkOptionsVisible(true);
    }

    private void OnSolidButtonClick(object? sender, RoutedEventArgs e)
    {
        CloseInsertFlyout();
        ShapePicker.IsVisible = false;
        SolidPicker.IsVisible = true;
        SetInkOptionsVisible(true);
    }

    private bool _clearSlideArmed;

    private void OnClearSlidePressed(object? sender, PointerPressedEventArgs e)
    {
        _clearSlideArmed = ClearPageSlider.Value < 5
            && e.GetPosition(ClearPageSlider).X < 45;
    }

    private void OnClearSlideReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool completed = _clearSlideArmed && ClearPageSlider.Value >= 98
            && e.GetPosition(ClearPageSlider).X > ClearPageSlider.Bounds.Width - 30;
        _clearSlideArmed = false;
        ClearPageSlider.Value = 0;
        if (!completed) return;
        EraserOptions.IsVisible = false;
        OnClearClick(sender, new RoutedEventArgs());
    }

    private void OnShapeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag
            && Enum.TryParse<BoardTool>(tag, out var tool))
        {
            SetTool(tool);
            SetInkOptionsVisible(true);
        }
    }

    /// <summary>形状选项里的「函数」：打开函数对话框，确认后把图像插到视野中央。</summary>
    private async void OnFunctionClick(object? sender, RoutedEventArgs e)
    {
        CloseInsertFlyout();
        SetInkOptionsVisible(false);
        var dialog = new FunctionDialog(null, Board.InkColor, Board.InkWidth);
        var spec = await dialog.ShowDialog<FunctionSpec?>(this);
        if (spec is null) return;

        var view = Board.VisibleWorldBounds;
        double width = Math.Min(view.Width * 0.62, 760);
        double height = Math.Min(view.Height * 0.58, 460);

        var item = new FunctionItem
        {
            XMin = spec.XMin,
            XMax = spec.XMax,
            YMin = spec.YMin,
            YMax = spec.YMax,
            Color = Board.InkColor,
            Width = Board.InkWidth,
            PlotWidth = width,
            PlotHeight = height,
            Left = view.Center.X - width / 2,
            Top = view.Center.Y - height / 2,
        };
        item.Curves = ToCurves(spec);

        Board.AddItem(item, select: true);
        SetTool(BoardTool.Select);
        MarkDirty();
        ShowToast(spec.Curves.Count > 1
            ? $"已插入 {spec.Curves.Count} 条函数曲线，双击可继续编辑"
            : "已插入函数图像，双击可调参数 a");
    }

    /// <summary>把对话框结果里的曲线描述转成画布对象使用的曲线。</summary>
    private static List<FunctionCurve> ToCurves(FunctionSpec spec)
    {
        var curves = new List<FunctionCurve>(spec.Curves.Count);
        foreach (var draft in spec.Curves)
        {
            curves.Add(new FunctionCurve
            {
                Latex = draft.Latex,
                Expression = draft.Expression,
                ParameterA = draft.ParameterA,
                Color = draft.Color,
            });
        }

        return curves;
    }

    /// <summary>插入三维几何体：选中后拖动可改变观察角度。</summary>
    private void OnSolidClick(object? sender, RoutedEventArgs e)
    {
        CloseInsertFlyout();
        string? tag = sender switch
        {
            Button { Tag: string buttonTag } => buttonTag,
            MenuItem { Tag: string menuTag } => menuTag,
            _ => null,
        };
        if (tag is null || !Enum.TryParse<SolidKind>(tag, out var kind))
        {
            return;
        }

        var view = Board.VisibleWorldBounds;
        double size = Math.Clamp(Math.Min(view.Width, view.Height) * 0.32, 80, 460);
        var item = new SolidItem
        {
            Kind = kind,
            CenterX = view.Center.X,
            CenterY = view.Center.Y,
            Size = size,
            Height = kind switch
            {
                SolidKind.Cuboid => size * 0.66,
                SolidKind.Pyramid => size * 0.95,
                SolidKind.Cylinder => size * 0.92,
                _ => size,
            },
            Color = Board.InkColor,
            Width = Board.InkWidth,
        };

        Board.AddItem(item, select: true);
        SetTool(BoardTool.Select);
        MarkDirty();
        ShowToast("选中状态下拖动几何体可三维旋转");
    }

    /// <summary>选择工具下双击已有的函数图像，重新打开参数面板修改表达式或 a。</summary>
    private async void OnBoardDoubleClicked(object? sender, Point world)
    {
        if (Board.PickAt(world) is not FunctionItem item) return;

        SetTool(BoardTool.Select);
        var dialog = new FunctionDialog(item, Board.InkColor, Board.InkWidth);
        var spec = await dialog.ShowDialog<FunctionSpec?>(this);
        if (spec is null) return;

        item.Curves = ToCurves(spec);
        item.XMin = spec.XMin;
        item.XMax = spec.XMax;
        item.YMin = spec.YMin;
        item.YMax = spec.YMax;
        Board.InvalidateVisual();
        MarkDirty();
    }

    private void OnColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string hex) return;
        Board.SetInkColor(Convert.ToUInt32(hex, 16));
        foreach (var swatch in _swatches)
        {
            swatch.Classes.Set("active", ReferenceEquals(swatch, button));
        }

        UpdateInkPreview();
    }

    private async void OnCustomColorClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new Window();
        SetupDialog(dialog, "自定义画笔颜色");
        dialog.MaxWidth = 640;
        dialog.SizeToContent = SizeToContent.WidthAndHeight;

        uint currentRgb = Board.InkColor & 0xFFFFFF;
        byte[] channels = [(byte)((currentRgb >> 16) & 0xFF), (byte)((currentRgb >> 8) & 0xFF), (byte)(currentRgb & 0xFF)];

        var preview = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(channels[0], channels[1], channels[2])),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var rgbPanel = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        Slider[] sliders = new Slider[3];
        TextBlock[] hexLabels = new TextBlock[3];
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var hl = new TextBlock
            {
                Text = channels[idx].ToString("X2"),
                FontSize = 12, FontWeight = FontWeight.Medium,
                Foreground = Brushes.LightGray,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 56, 0),
            };

            var sl = new Slider
            {
                Value = channels[idx], Minimum = 0, Maximum = 255,
                Width = 260, Margin = new Thickness(0, 4, 0, 8), Foreground = Brushes.White,
            };

            sl.ValueChanged += (_, __) =>
            {
                byte val = (byte)Math.Round(sl.Value);
                channels[idx] = val;
                hl.Text = val.ToString("X2");
                preview.Background = new SolidColorBrush(Color.FromRgb(channels[0], channels[1], channels[2]));
            };

            var label = new TextBlock { Text = i == 0 ? "R" : i == 1 ? "G" : "B", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Width = 18 };
            rgbPanel.Children.Add(new StackPanel { Spacing = 2, Children = { label, sl, hl } });
            sliders[i] = sl;
            hexLabels[i] = hl;
        }

        void ApplyPreset(uint color)
        {
            byte r = (byte)((color >> 16) & 0xFF);
            byte g = (byte)((color >> 8) & 0xFF);
            byte b = (byte)(color & 0xFF);
            channels[0] = r; channels[1] = g; channels[2] = b;
            sliders[0].Value = r; sliders[1].Value = g; sliders[2].Value = b;
            hexLabels[0].Text = r.ToString("X2");
            hexLabels[1].Text = g.ToString("X2");
            hexLabels[2].Text = b.ToString("X2");
            preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        var palette = new StackPanel { Spacing = 8 };

        void AddSection(string title, uint[] colors)
        {
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = title, FontSize = 11, FontWeight = FontWeight.Medium,
                Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2),
            });
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (uint c in colors)
            {
                var sw = new Button
                {
                    Width = 28, Height = 28, Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(14),
                    Background = new SolidColorBrush(Color.FromUInt32(c)),
                    BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
                };
                sw.Click += (_, _) => ApplyPreset(c);
                row.Children.Add(sw);
            }
            section.Children.Add(row);
            palette.Children.Add(section);
        }

        // 黑白灰
        AddSection("黑白灰", new[] { 0xFF000000u, 0xFF2B2B2B, 0xFF4A4A4A, 0xFF6E6E6E, 0xFF999999, 0xFFBBBBBB, 0xFFDDDDDD, 0xFFFFFFFF });
        // 红色系
        AddSection("红", new[] { 0xFF8B0000u, 0xFFC0392B, 0xFFE74C3C, 0xFFFF6B6B, 0xFFFA5454, 0xFFFF8A8A, 0xFFFFCDCD, 0xFFFCE4EC });
        // 橙色系
        AddSection("橙", new[] { 0xFFE65100u, 0xFFEF6C00, 0xFFFA7404, 0xFFFF922B, 0xFFFFA726, 0xFFFFD180, 0xFFFFE0B2, 0xFFFFF3E0 });
        // 黄色系
        AddSection("黄", new[] { 0xFFFDD835u, 0xFFFFEA00, 0xFFFFD54A, 0xFFFFEE55, 0xFFFFF176, 0xFFFFF9C4, 0xFFFFFF80, 0xFFFFFFB0 });
        // 绿色系
        AddSection("绿", new[] { 0xFF2E7D32u, 0xFF43A047, 0xFF66BB6A, 0xFF51CF66, 0xFF81C784, 0xFFA5D6A7, 0xFFC8E6C9, 0xFFE8F5E9 });
        // 青色系
        AddSection("青", new[] { 0xFF0083B0u, 0xFF00ACC1, 0xFF26C6DA, 0xFF3BC9DB, 0xFF4DD0E1, 0xFF80DEEA, 0xFFB2EBF0, 0xFFE0F7FA });
        // 蓝色系
        AddSection("蓝", new[] { 0xFF1A237Eu, 0xFF3949AB, 0xFF4DABF7, 0xFF5C6BC0, 0xFF7986CB, 0xFF9FA8DA, 0xFFBBDEFB, 0xFFE3F2FD });
        // 紫色系
        AddSection("紫", new[] { 0xFF4A148Cu, 0xFF7B1FA2, 0xFFAB47BC, 0xFFB197FC, 0xFFCE93D8, 0xFFE1BEE7, 0xFFEA8CFC, 0xFFF3E5F5 });
        // 粉/玫红
        AddSection("粉", new[] { 0xFFAD1457u, 0xFFC2185B, 0xFFEC407A, 0xFFFF80AB, 0xFFE91E63, 0xFFF48FB1, 0xFFFCE4EC, 0xFFFAE4EF });
        // 棕色/大地
        AddSection("棕", new[] { 0xFF3E2723u, 0xFF5D4037, 0xFF8D6E63, 0xFF99582A, 0xFFA1887F, 0xFFBCAAA4, 0xFFD7CCC8, 0xFFFBE9E7 });

        // 左侧：预览 + RGB 滑块 + 确认按钮
        var confirm = new Button { Content = "应用颜色", HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) =>
        {
            var bg = (SolidColorBrush)preview.Background;
            dialog.Close(0xFF000000u | ((uint)bg.Color.R << 16) | ((uint)bg.Color.G << 8) | (uint)bg.Color.B);
        };

        var title = new TextBlock { Text = "选择画笔颜色", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) };

        // 左侧：预览 + RGB 滑块 + 确认按钮
        var leftPanel = new StackPanel { Spacing = 12 };
        leftPanel.Children.Add(preview);
        leftPanel.Children.Add(rgbPanel);
        leftPanel.Children.Add(confirm);

        // 右侧：色板（滚动）
        var paletteScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        paletteScroll.Content = palette;

        // 左右并排
        var contentPanel = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24),
            Children =
            {
                title,
                new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    },
                    RowDefinitions =
                    {
                        new RowDefinition(),
                        new RowDefinition { Height = GridLength.Auto },
                    },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        leftPanel,
                        paletteScroll,
                    },
                },
            },
        };

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(paletteScroll, 1);

        dialog.Content = contentPanel;

        // 根据可用宽度自动折叠/展开色板内容
        double? savedWidth = null;
        bool isWide = true;
        dialog.SizeChanged += (s, ev) =>
        {
            var w = ev.NewSize.Width;
            if (w > 0 && savedWidth.HasValue && Math.Abs(w - savedWidth.Value) < 1) return;
            savedWidth = w;
            bool narrow = w < 400;
            if (narrow && isWide)
            {
                // 变窄：上下排列
                Grid.SetRow(paletteScroll, 1);
                paletteScroll.MaxHeight = w - 120;
            }
            else if (!narrow && !isWide)
            {
                // 变宽：左右并排
                Grid.SetRow(paletteScroll, 0);
                paletteScroll.MaxHeight = double.NaN;
            }
            isWide = !narrow;
        };

        var result = await dialog.ShowDialog<uint?>(this);
        if (result is not { } color) return;
        CustomSwatch.Tag = color.ToString("X8");
        CustomSwatch.Background = new SolidColorBrush(Color.FromUInt32(color));
        OnColorClick(CustomSwatch, e);
    }

    private void OnInkWidthChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (Board is null) return;
        Board.SetInkWidth(e.NewValue);
        UpdateInkPreview();
    }

    /// <summary>粗细预览：条高与颜色实时跟随当前笔（荧光笔按半透明加粗显示）。</summary>
    private void UpdateInkPreview()
    {
        if (Board is null || InkPreview is null) return;

        bool highlighter = _tool == BoardTool.Highlighter;
        double thickness = Math.Clamp(Board.InkWidth * (highlighter ? 2.4 : 1.0), 1, 24);

        InkPreview.Height = thickness;
        InkPreview.CornerRadius = new CornerRadius(thickness / 2);

        uint argb = Board.InkColor;
        byte a = (byte)(argb >> 24);
        if (highlighter) a = (byte)(a * 0.32);
        InkPreview.Background = new SolidColorBrush(Color.FromArgb(
            a, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    private void OnGridMenuClick(object? sender, RoutedEventArgs e)
    {
        Board.SetShowGrid(!Board.Document.ShowGrid);
        UpdateGridMenu();
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e) => Board.Undo();

    private void OnRedoClick(object? sender, RoutedEventArgs e) => Board.Redo();

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (Board.Document.Items.Count == 0)
        {
            ShowToast("当前页已经是空白");
            return;
        }

        Board.ClearBoard();
        ShowToast("已清空本页，可使用撤销恢复");
    }

    private void OnPrevPageClick(object? sender, RoutedEventArgs e) => AnimatePageChange(Board.PreviousPage, -1);

    private void OnNextPageClick(object? sender, RoutedEventArgs e) => AnimatePageChange(Board.NextPage, 1);

    private void OnAddPageClick(object? sender, RoutedEventArgs e)
    {
        AnimatePageChange(() =>
        {
            Board.AddPage();
            MarkDirty();
            ShowToast($"已新建第 {Board.PageNumber} 页");
        }, 1);
    }

    private async void OnDeletePageClick(object? sender, RoutedEventArgs e)
    {
        if (Board.PageCount <= 1)
        {
            ShowToast("至少保留一页");
            return;
        }

        if (!await ConfirmAsync("删除本页？", "此操作会清除当前撤销历史，且无法撤销。", "删除")) return;
        AnimatePageChange(() =>
        {
            Board.DeleteCurrentPage();
            MarkDirty();
            ShowToast("已删除本页");
        }, Board.PageNumber == Board.PageCount ? -1 : 1);
    }

    private async void AnimatePageChange(Action change, int direction)
    {
        if (_pageAnimating) return;
        _pageAnimating = true;
        Board.IsHitTestVisible = false;
        try
        {
            Board.Opacity = 0;
            Board.RenderTransform = TransformOperations.Parse($"translateX({-direction * 18}px)");
            await Task.Delay(160);
            change();
            Board.RenderTransform = TransformOperations.Parse($"translateX({direction * 18}px)");
            Dispatcher.UIThread.Post(() =>
            {
                Board.Opacity = 1;
                Board.RenderTransform = TransformOperations.Parse("translateX(0px)");
            }, DispatcherPriority.Render);
            await Task.Delay(180);
        }
        finally
        {
            Board.Opacity = 1;
            Board.RenderTransform = TransformOperations.Parse("translateX(0px)");
            Board.IsHitTestVisible = true;
            _pageAnimating = false;
        }
    }

    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => Board.ResetView();

    private void OnFitClick(object? sender, RoutedEventArgs e) => Board.FitToContent();

    private async void OnExitClick(object? sender, RoutedEventArgs e)
    {
        if (_dirty && !await ConfirmAsync("退出 SETBoard？", "尚未保存的白板内容将会丢失。", "退出")) return;
        Close();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => MinimizeToDesktop();

    private DesktopInkBar? _inkBar;
    private DesktopAnnotationWindow? _annotation;

    /// <summary>隐藏主窗口，在桌面留下可批注的浮窗。</summary>
    private void MinimizeToDesktop()
    {
        _inkBar ??= CreateInkBar();
        Hide();
        _inkBar.ShowAtDesktopCorner();
    }

    /// <summary>以浮窗模式启动（--float 参数），不显示主界面。</summary>
    public void StartInFloatingMode()
    {
        _inkBar ??= CreateInkBar();
        _inkBar.ShowAtDesktopCorner();
    }

    private DesktopInkBar CreateInkBar()
    {
        var bar = new DesktopInkBar();
        bar.AnnotateRequested += (_, _) =>
        {
            bar.Hide();
            _annotation?.Close();
            _annotation = new DesktopAnnotationWindow();
            _annotation.Closed += (_, _) =>
            {
                _annotation = null;
                if (!IsVisible) bar.ShowAtDesktopCorner();
            };
            _annotation.Show();
            _annotation.Activate();
        };
        bar.RestoreRequested += (_, _) =>
        {
            bar.Hide();
            Show();
            WindowState = WindowState.FullScreen;
            Activate();
        };
        bar.ExitRequested += (_, _) =>
        {
            bar.Close();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        };
        return bar;
    }

    private void OnFullScreenClick(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>版本号来自 version.json（发版时由 Action 改写），读不到时退回程序集版本。</summary>
    private static string AppVersion
    {
        get
        {
            using var stream = typeof(MainWindow).Assembly
                .GetManifestResourceStream("SETBoard.version.json");
            if (stream is not null)
            {
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("version", out var value)
                    && value.GetString() is { Length: > 0 } text)
                {
                    return text;
                }
            }

            return typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new Window();
        SetupDialog(dialog, "关于 SETBoard");

        // 标题栏：图标 + 标题文字
        var iconBorder = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            Background = AppIcon,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(iconBorder);
        titleRow.Children.Add(new TextBlock
        {
            Text = "关于 SETBoard", FontSize = 18, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F2F6F9")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var panel = new StackPanel { Margin = new Thickness(28, 18, 28, 22), Spacing = 10 };
        panel.Children.Add(titleRow);
        panel.Children.Add(new TextBlock
        {
            Text = "Swift Education Technology",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9FB4C1")),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "迅教科技",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#7E93A1")),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"版本 {AppVersion}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6F8493")),
        });

        // 手动检查更新按钮
        var checkUpdateBtn = CreateDialogButton("检查更新", "#33424E", "#FFD54A");
        checkUpdateBtn.Click += async (_, _) =>
        {
            checkUpdateBtn.IsEnabled = false;
            checkUpdateBtn.Content = "检查中…";

            bool hasUpdate = await AutoUpdater.CheckAsync(_ => { });
            if (hasUpdate)
            {
                new UpdateDialog(this).Show(this);
            }
            else
            {
                checkUpdateBtn.Content = "已是最新版";
                Dispatcher.UIThread.Post(() =>
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (_, __) =>
                    {
                        timer.Stop();
                        checkUpdateBtn.IsEnabled = true;
                        checkUpdateBtn.Content = "检查更新";
                    };
                    timer.Start();
                });
            }
        };
        panel.Children.Add(checkUpdateBtn);

        var closeBtn = CreateDialogButton("关闭");
        closeBtn.Click += (_, _) => dialog.Close();
        panel.Children.Add(closeBtn);

        dialog.Content = panel;
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        WindowFx.FadeIn(dialog);
        _ = dialog.ShowDialog(this);
    }

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Maximized
            : WindowState.FullScreen;
    }

    private static FilePickerFileType BoardFileType => new("SETBoard 白板")
    {
        Patterns = new[] { "*.setb" },
    };

    private static FilePickerFileType PngFileType => new("PNG 图片")
    {
        Patterns = new[] { "*.png" },
    };

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存白板",
                SuggestedFileName = "白板",
                DefaultExtension = "setb",
                FileTypeChoices = new[] { BoardFileType },
            });
            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            BoardSerializer.Save(Board.Document, stream);
            _dirty = false;
            UpdateTitle();
            ShowToast("已保存");
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}");
        }
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_dirty && !await ConfirmAsync("打开其他白板？", "当前未保存的内容将会被替换。", "继续打开")) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开白板",
                AllowMultiple = false,
                FileTypeFilter = new[] { BoardFileType },
            });
            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            var document = BoardSerializer.Load(stream);
            Board.SetDocument(document);
            Board.ResetView();
            _dirty = false;
            UpdateTitle();
            UpdatePageLabel();
            UpdateGridMenu();
            ShowToast("已打开白板");
        }
        catch (Exception ex)
        {
            ShowToast($"打开失败：{ex.Message}");
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出图片",
                SuggestedFileName = "白板",
                DefaultExtension = "png",
                FileTypeChoices = new[] { PngFileType },
            });
            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            Board.ExportPng(stream);
            ShowToast("图片已导出");
        }
        catch (Exception ex)
        {
            ShowToast($"导出失败：{ex.Message}");
        }
    }

    private async void OnInsertImageClick(object? sender, RoutedEventArgs e)
    {
        CloseInsertFlyout();
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "插入图片",
                AllowMultiple = true,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
            });
            if (files.Count == 0) return;

            var view = Board.VisibleWorldBounds;
            double maxWidth = view.Width * 0.7;
            double maxHeight = view.Height * 0.7;
            int inserted = 0;

            foreach (var file in files)
            {
                try
                {
                    await using var stream = await file.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    var data = buffer.ToArray();

                    var size = Measure(data);
                    if (size is null)
                    {
                        continue;
                    }

                    double scale = Math.Min(1, Math.Min(maxWidth / size.Value.Width, maxHeight / size.Value.Height));
                    double width = size.Value.Width * scale;
                    double height = size.Value.Height * scale;
                    double shift = inserted * 24;

                    var item = new ImageItem
                    {
                        Data = data,
                        X = view.Center.X - width / 2 + shift,
                        Y = view.Center.Y - height / 2 + shift,
                        Width = width,
                        Height = height,
                    };

                    Board.AddItem(item, select: true);
                    inserted++;
                }
                catch
                {
                    // 单张失败不影响其余图片
                }
            }

            if (inserted == 0)
            {
                ShowToast("无法识别的图片格式");
                return;
            }

            SetTool(BoardTool.Select);
            _dirty = true;
            UpdateTitle();
            ShowToast(inserted > 1 ? $"已插入 {inserted} 张图片" : "已插入图片");
        }
        catch (Exception ex)
        {
            ShowToast($"插入失败：{ex.Message}");
        }
    }

    private void CloseInsertFlyout() => InsertButton.Flyout?.Hide();

    /// <summary>只读取图片尺寸，不解码到画布。</summary>
    private static Size? Measure(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var bitmap = new Bitmap(stream);
            return new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        }
        catch
        {
            return null;
        }
    }

    private void ShowToast(string message)
    {
        int token = ++_toastToken;
        ToastText.Text = message;
        Toast.Opacity = 0;
        Toast.RenderTransform = TransformOperations.Parse("translateY(-8px)");
        Toast.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            Toast.Opacity = 1;
            Toast.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Render);

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) =>
        {
            if (token != _toastToken) return;
            _toastTimer?.Stop();
            _toastTimer = null;
            Toast.Opacity = 0;
            Toast.RenderTransform = TransformOperations.Parse("translateY(-8px)");
            var hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                if (token == _toastToken) Toast.IsVisible = false;
            };
            hide.Start();
        };
        _toastTimer.Start();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new Window();
        SetupDialog(dialog, title);

        // 标题栏：图标 + 标题文字
        var iconBorder = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            Background = AppIcon,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(iconBorder);
        titleRow.Children.Add(new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F2F6F9")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var panel = new StackPanel { Margin = new Thickness(28, 18, 28, 22), Spacing = 10 };
        panel.Children.Add(titleRow);
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#9FB4C1")),
        });

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        var cancelBtn = CreateDialogButton("取消");
        var confirmBtn = CreateDialogButton(confirmText, "#C94B50");
        actionsPanel.Children.Add(cancelBtn);
        actionsPanel.Children.Add(confirmBtn);
        panel.Children.Add(actionsPanel);

        cancelBtn.Click += (_, _) => dialog.Close(false);
        confirmBtn.Click += (_, _) => dialog.Close(true);

        dialog.Content = panel;
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(false); };
        WindowFx.FadeIn(dialog, 160);
        return await dialog.ShowDialog<bool>(this);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    if (shift) Board.Redo(); else Board.Undo();
                    e.Handled = true;
                    return;
                case Key.Y:
                    Board.Redo();
                    e.Handled = true;
                    return;
                case Key.S:
                    OnSaveClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.O:
                    OnOpenClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.D0:
                    Board.ResetView();
                    e.Handled = true;
                    return;
                case Key.D1:
                    Board.FitToContent();
                    e.Handled = true;
                    return;
                case Key.Add:
                case Key.OemPlus:
                    Board.ZoomBy(1.25);
                    e.Handled = true;
                    return;
                case Key.Subtract:
                case Key.OemMinus:
                    Board.ZoomBy(0.8);
                    e.Handled = true;
                    return;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                Board.DeleteSelection();
                e.Handled = true;
                break;
            case Key.PageUp:
                AnimatePageChange(Board.PreviousPage, -1);
                e.Handled = true;
                break;
            case Key.PageDown:
                AnimatePageChange(Board.NextPage, 1);
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen)
                {
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    SetTool(BoardTool.Select);
                }

                e.Handled = true;
                break;
            case Key.V:
                SetTool(BoardTool.Select);
                break;
            case Key.M:
                SetTool(BoardTool.Pan);
                break;
            case Key.P:
                SetTool(BoardTool.Pen);
                break;
            case Key.H:
                SetTool(BoardTool.Highlighter);
                break;
            case Key.E:
                SetTool(BoardTool.Eraser);
                break;
            case Key.L:
                SetTool(BoardTool.Line);
                break;
            case Key.A:
                SetTool(BoardTool.Arrow);
                break;
            case Key.R:
                SetTool(BoardTool.Rectangle);
                break;
            case Key.O:
                SetTool(BoardTool.Ellipse);
                break;
            case Key.T:
                SetTool(BoardTool.Triangle);
                break;
        }
    }
}
