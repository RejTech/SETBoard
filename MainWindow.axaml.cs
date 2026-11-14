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
    private readonly Button[] _toolButtons;
    private readonly Button[] _swatches;

    private DispatcherTimer? _toastTimer;
    private bool _dirty;
    private BoardTool _lastShape = BoardTool.Rectangle;
    private BoardTool _tool = BoardTool.Pen;
    private bool _inkOptionsOpen;
    private int _inkOptionsToken;

    public MainWindow()
    {
        InitializeComponent();

        _toolButtons = new[]
        {
            PenButton, HighlighterButton, EraserButton, SelectButton, FillButton, ShapeButton,
        };
        _swatches = new[]
        {
            WhiteSwatch, YellowSwatch, RedSwatch, GreenSwatch, BlueSwatch, GraySwatch,
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

        // 点击工具条以外的地方（画布、其他按钮、菜单等）收起二级选项菜单
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, true);

        // 触屏/触控笔直读：落笔与轨迹不再等 Avalonia 的指针事件排队
        Opened += (_, _) => _rawInput ??= RawPointerInput.Attach(this, OnRawPointerSample);
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
        if (!_inkOptionsOpen) return;
        if (e.Source is Visual source && IsInside(source, ToolDock)) return;
        SetInkOptionsVisible(false);
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
        GridMenuItem.Header = Board.Document.ShowGrid ? "点阵：开" : "点阵：关";
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
            if (button == ShapeButton) active = isShape;
            button.Classes.Set("active", active);
        }

        foreach (var button in ShapePicker.Children.OfType<Button>())
        {
            button.Classes.Set("active", button.Tag is string tag
                                         && Enum.TryParse<BoardTool>(tag, out var value)
                                         && value == tool);
        }

        ShapePicker.IsVisible = isShape;
        SetInkOptionsVisible(false);
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
        if (IsShapeTool(_tool))
        {
            ToggleInkOptions();
            return;
        }

        SetTool(_lastShape);
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
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<SolidKind>(tag, out var kind))
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

    private void OnPrevPageClick(object? sender, RoutedEventArgs e) => Board.PreviousPage();

    private void OnNextPageClick(object? sender, RoutedEventArgs e) => Board.NextPage();

    private void OnAddPageClick(object? sender, RoutedEventArgs e)
    {
        Board.AddPage();
        MarkDirty();
        ShowToast($"已新建第 {Board.PageNumber} 页");
    }

    private void OnDeletePageClick(object? sender, RoutedEventArgs e)
    {
        if (Board.PageCount <= 1)
        {
            ShowToast("至少保留一页");
            return;
        }

        Board.DeleteCurrentPage();
        MarkDirty();
        ShowToast("已删除本页");
    }

    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => Board.ResetView();

    private void OnFitClick(object? sender, RoutedEventArgs e) => Board.FitToContent();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

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
        var dialog = new Window
        {
            Title = "关于 SETBoard",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0C1B24")),
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
        };

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 22), Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "SETBoard 白板",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#F2F6F9")),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Swift Education Technology",
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
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
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#6F8493")),
        });

        var close = new Button
        {
            Content = "关闭",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(22, 7, 22, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#33424E")),
            Foreground = Brushes.White,
        };
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(close);

        dialog.Content = panel;
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) dialog.Close();
        };
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
            _toastTimer?.Stop();
            _toastTimer = null;
            Toast.Opacity = 0;
            Toast.RenderTransform = TransformOperations.Parse("translateY(-8px)");
            var hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                Toast.IsVisible = false;
            };
            hide.Start();
        };
        _toastTimer.Start();
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
                Board.PreviousPage();
                e.Handled = true;
                break;
            case Key.PageDown:
                Board.NextPage();
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
