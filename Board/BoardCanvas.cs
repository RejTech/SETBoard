using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SETBoard.Board;

public enum BoardTool
{
    Pan,
    Select,
    Pen,
    Highlighter,
    Eraser,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Triangle,
    Fill,
}

/// <summary>白板画布：无限画布、墨迹、图形、选择与缩放。</summary>
public sealed partial class BoardCanvas : Control
{
    private const double MinZoom = 0.15;
    private const double MaxZoom = 6.0;

    /// <summary>橡皮的擦除半径，按屏幕像素固定，缩放时手感一致。</summary>
    private const double EraseRadiusScreen = 14;

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly BoardDocument _document = new();
    private double _zoom = 1.0;
    private Vector _offset;
    private bool _cameraInitialized;
    private int _viewAnimationToken;

    private readonly SolidColorBrush _backgroundBrush = new(Colors.White);
    private IBrush? _dotBrush;
    private Color _dotColor;

    public BoardCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = CrossCursor;
    }

    public event EventHandler? DocumentChanged;
    public event EventHandler? ViewChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? PageChanged;

    public BoardDocument Document => _document;
    public UndoStack History { get; } = new();
    public BoardTool Tool { get; private set; } = BoardTool.Pen;
    public uint InkColor { get; private set; } = 0xFFFFFFFF;
    public double InkWidth { get; private set; } = 4;
    public double Zoom => _zoom;
    public bool ShowGrid => _document.ShowGrid;

    public IReadOnlyCollection<BoardItem> Selection => _selection;

    public void SetTool(BoardTool tool)
    {
        Tool = tool;
        if (tool != BoardTool.Eraser) _eraserScreen = null;
        Cursor = tool switch
        {
            BoardTool.Select => ArrowCursor,
            BoardTool.Pan => HandCursor,
            BoardTool.Eraser => HandCursor,
            BoardTool.Fill => HandCursor,
            _ => CrossCursor,
        };
        InvalidateVisual();
    }

    public void SetInkColor(uint color)
    {
        InkColor = color;
    }

    public void SetInkWidth(double width)
    {
        InkWidth = Math.Clamp(width, 1, 40);
    }

    public void SetShowGrid(bool show)
    {
        _document.ShowGrid = show;
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public int PageCount => _document.Pages.Count;
    public int PageNumber => _document.CurrentPageIndex + 1;

    /// <summary>跳转到指定页码（从 1 开始）。</summary>
    public void GoToPage(int number)
    {
        int target = Math.Clamp(number - 1, 0, PageCount - 1);
        if (target == _document.CurrentPageIndex) return;
        CancelActiveInteraction();
        _selection.Clear();
        History.Clear();
        _document.CurrentPageIndex = target;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void PreviousPage() => GoToPage(PageNumber - 1);

    public void NextPage() => GoToPage(PageNumber + 1);

    public void AddPage()
    {
        int insertAt = _document.CurrentPageIndex + 1;
        _document.Pages.Insert(insertAt, new BoardPage());
        History.Clear();
        _document.CurrentPageIndex = insertAt;
        _selection.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteCurrentPage()
    {
        if (PageCount <= 1) return;
        CancelActiveInteraction();
        int index = Math.Clamp(_document.CurrentPageIndex, 0, PageCount - 1);
        _document.Pages.RemoveAt(index);
        _document.CurrentPageIndex = Math.Min(index, PageCount - 1);
        _selection.Clear();
        History.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SetDocument(BoardDocument document, bool resetView = true)
    {
        CancelActiveInteraction();
        _document.ReplaceWith(document);
        _selection.Clear();
        History.Clear();
        if (resetView)
        {
            _cameraInitialized = false;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        PageChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Undo()
    {
        History.Undo(_document);
        _selection.RemoveWhere(item => !_document.Items.Contains(item));
        OnHistoryChanged();
    }

    public void Redo()
    {
        History.Redo(_document);
        _selection.RemoveWhere(item => !_document.Items.Contains(item));
        OnHistoryChanged();
    }

    private void OnHistoryChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        var removed = _selection.ToList();
        foreach (var item in removed)
        {
            _document.Items.Remove(item);
        }

        _selection.Clear();
        History.Push(new ChangeCommand(removed, Array.Empty<BoardItem>()));
        OnHistoryChanged();
    }

    public void ClearBoard()
    {
        if (_document.Items.Count == 0) return;
        var removed = _document.Items.ToList();
        _document.Items.Clear();
        _selection.Clear();
        History.Push(new ChangeCommand(removed, Array.Empty<BoardItem>()));
        OnHistoryChanged();
    }

    public void ResetView()
    {
        AnimateView(1.0, new Vector(Bounds.Width / 2, Bounds.Height / 2));
    }

    public void FitToContent()
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var content = _document.ContentBounds(64);
        if (content.Width < 1 || content.Height < 1)
        {
            ResetView();
            return;
        }

        double zoom = Math.Min(Bounds.Width / content.Width, Bounds.Height / content.Height);
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var offset = new Vector(
            Bounds.Width / 2 - (content.X + content.Width / 2) * zoom,
            Bounds.Height / 2 - (content.Y + content.Height / 2) * zoom);
        AnimateView(zoom, offset);
    }

    private void AnimateView(double targetZoom, Vector targetOffset)
    {
        int token = ++_viewAnimationToken;
        double startZoom = _zoom;
        Vector startOffset = _offset;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (token != _viewAnimationToken)
            {
                timer.Stop();
                return;
            }

            double progress = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 220, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            _zoom = startZoom + (targetZoom - startZoom) * eased;
            _offset = new Vector(
                startOffset.X + (targetOffset.X - startOffset.X) * eased,
                startOffset.Y + (targetOffset.Y - startOffset.Y) * eased);
            _cameraInitialized = true;
            ViewChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            if (progress < 1) return;
            timer.Stop();
        };
        timer.Start();
    }

    public void ZoomBy(double factor, Point? anchor = null)
    {
        _viewAnimationToken++;
        var screenAnchor = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var worldAnchor = ToWorld(screenAnchor);
        double next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _zoom = next;
        _offset = new Vector(
            screenAnchor.X - worldAnchor.X * _zoom,
            screenAnchor.Y - worldAnchor.Y * _zoom);
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private Point ToWorld(Point screen) => new(
        (screen.X - _offset.X) / _zoom,
        (screen.Y - _offset.Y) / _zoom);

    private Point ToScreen(Point world) => new(
        world.X * _zoom + _offset.X,
        world.Y * _zoom + _offset.Y);

    private Rect VisibleWorldRect()
    {
        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(Bounds.Width, Bounds.Height));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>当前可见区域（世界坐标），用于把新内容放到视野中央。</summary>
    public Rect VisibleWorldBounds => VisibleWorldRect();

    /// <summary>按世界坐标从上层往下找命中的对象（用于双击编辑等场景）。</summary>
    public BoardItem? PickAt(Point world)
    {
        for (int i = _document.Items.Count - 1; i >= 0; i--)
        {
            if (_document.Items[i].HitTest(world, 8 / _zoom)) return _document.Items[i];
        }

        return null;
    }

    /// <summary>新增对象并登记到撤销栈；可选择是否立即选中它。</summary>
    public void AddItem(BoardItem item, bool select = false)
    {
        _document.Items.Add(item);
        History.Push(new ChangeCommand(Array.Empty<BoardItem>(), new[] { item }));

        if (select)
        {
            _selection.Clear();
            _selection.Add(item);
        }

        OnHistoryChanged();
    }

    public override void Render(DrawingContext context)
    {
        var viewBounds = new Rect(Bounds.Size);
        var background = Color.FromUInt32(_document.Background);
        if (background.A > 0)
        {
            _backgroundBrush.Color = background;
            context.FillRectangle(_backgroundBrush, viewBounds);
        }

        if (!_cameraInitialized && Bounds.Width > 1)
        {
            _offset = new Vector(Bounds.Width / 2, Bounds.Height / 2);
            _cameraInitialized = true;
        }

        if (_document.ShowGrid)
        {
            DrawGrid(context, viewBounds);
        }

        var visible = VisibleWorldRect();
        using (context.PushTransform(
                   Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            foreach (var item in _document.Items)
            {
                if (item.Bounds.Intersects(visible))
                {
                    item.Draw(context);
                }
            }

            _liveStroke?.Draw(context);
            _previewShape?.Draw(context);
        }

        DrawSelection(context);
        DrawMarquee(context);
        DrawEraserIndicator(context);
    }

    /// <summary>橡皮的擦除范围指示：与擦除半径等大的圆圈，跟随指针。</summary>
    private void DrawEraserIndicator(DrawingContext context)
    {
        if ((Tool != BoardTool.Eraser && _interaction != Interaction.Erasing)
            || _eraserScreen is not { } center) return;

        // 板面可能是深色也可能是浅色，圈的颜色跟着反着取，保证看得清
        var background = Color.FromUInt32(_document.Background);
        bool dark = background.R * 0.299 + background.G * 0.587 + background.B * 0.114 < 128;
        byte value = dark ? (byte)0xFF : (byte)0x12;
        var fill = new SolidColorBrush(Color.FromArgb(0x1A, value, value, value)).ToImmutable();
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, value, value, value)), 1.2).ToImmutable();

        context.DrawEllipse(fill, stroke, center, EraseRadiusScreen, EraseRadiusScreen);
        context.DrawEllipse(stroke.Brush, null, center, 1, 1);
    }

    /// <summary>点阵背景：深色板面上呈现低对比度网格点。</summary>
    private void DrawGrid(DrawingContext context, Rect viewBounds)
    {
        double spacing = _document.GridSize * _zoom;
        while (spacing < 16) spacing *= 2;
        if (spacing < 1) return;

        var background = Color.FromUInt32(_document.Background);
        bool dark = (background.R * 0.299 + background.G * 0.587 + background.B * 0.114) < 128;
        var color = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(255, 214, 218, 224);
        if (_dotBrush is null || _dotColor != color)
        {
            _dotColor = color;
            _dotBrush = new SolidColorBrush(color).ToImmutable();
        }

        const double radius = 1.15;
        double startX = _offset.X % spacing;
        if (startX > 0) startX -= spacing;
        double startY = _offset.Y % spacing;
        if (startY > 0) startY -= spacing;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (double x = startX; x <= viewBounds.Width; x += spacing)
            {
                for (double y = startY; y <= viewBounds.Height; y += spacing)
                {
                    ctx.BeginFigure(new Point(x - radius, y - radius), true);
                    ctx.LineTo(new Point(x + radius, y - radius));
                    ctx.LineTo(new Point(x + radius, y + radius));
                    ctx.LineTo(new Point(x - radius, y + radius));
                    ctx.EndFigure(true);
                }
            }
        }

        context.DrawGeometry(_dotBrush, null, geometry);
    }

    private void DrawSelection(DrawingContext context)
    {
        if (_selection.Count == 0) return;

        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0x7E, 0xC4, 0xE8)), 1.5,
            new DashStyle(new double[] { 4, 3 }, 0)).ToImmutable();
        var fill = new SolidColorBrush(Color.FromArgb(0x14, 0x7E, 0xC4, 0xE8)).ToImmutable();

        foreach (var item in _selection)
        {
            var b = item.Bounds;
            var p1 = ToScreen(new Point(b.X, b.Y));
            var p2 = ToScreen(new Point(b.Right, b.Bottom));
            var rect = new Rect(p1, p2).Inflate(4);
            context.DrawRectangle(fill, stroke, rect);
        }

        if (_selection.Count == 1 && _selection.First() is { } selected
            && GetResizeHandle(selected) is { } handle)
        {
            var background = new SolidColorBrush(Color.FromRgb(0x7E, 0xC4, 0xE8));
            context.DrawRectangle(background, new Pen(Brushes.White, 1.5),
                new Rect(handle.X - 8, handle.Y - 8, 16, 16), 4, 4);
        }
    }

    private Point? GetResizeHandle(BoardItem item)
    {
        if (item is not (ImageItem or FunctionItem or SolidItem or ShapeItem)) return null;
        var rect = item switch
        {
            FunctionItem function => function.PlotArea,
            ShapeItem shape => shape.Rect,
            _ => item.Bounds,
        };
        return ToScreen(new Point(rect.Right, rect.Bottom)) + new Vector(4, 4);
    }

    private void DrawMarquee(DrawingContext context)
    {
        if (_interaction != Interaction.Marquee) return;
        var rect = new Rect(_marqueeStart, _marqueeCurrent).Normalize();
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(0x1A, 0x7E, 0xC4, 0xE8)),
            new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x9F, 0xD8, 0xF2)), 1),
            rect);
    }

    private BoardItem? PickItem(Point world, double tolerance)
    {
        for (int i = _document.Items.Count - 1; i >= 0; i--)
        {
            var item = _document.Items[i];
            if (item.HitTest(world, tolerance)) return item;
        }

        return null;
    }

    private void EnsureCamera()
    {
        if (!_cameraInitialized && Bounds.Width > 1)
        {
            _offset = new Vector(Bounds.Width / 2, Bounds.Height / 2);
            _cameraInitialized = true;
        }
    }

    public void ExportPng(Stream stream)
    {
        var content = _document.ContentBounds(48);
        if (content.Width < 1 || content.Height < 1)
        {
            content = new Rect(0, 0, 960, 600);
        }

        const double scale = 2.0;
        int width = (int)Math.Clamp(Math.Ceiling(content.Width * scale), 1, 8192);
        int height = (int)Math.Clamp(Math.Ceiling(content.Height * scale), 1, 8192);

        var exportView = new ExportView(_document, content, scale)
        {
            Width = width,
            Height = height,
        };
        exportView.Measure(new Size(width, height));
        exportView.Arrange(new Rect(0, 0, width, height));

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(exportView);
        bitmap.Save(stream);
    }

    /// <summary>离屏导出视图，直接按内容范围绘制。</summary>
    private sealed class ExportView : Control
    {
        private readonly BoardDocument _document;
        private readonly Rect _content;
        private readonly double _scale;
        private readonly SolidColorBrush _background;

        public ExportView(BoardDocument document, Rect content, double scale)
        {
            _document = document;
            _content = content;
            _scale = scale;
            _background = new SolidColorBrush(Color.FromUInt32(document.Background));
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(_background, new Rect(Bounds.Size));
            using (context.PushTransform(
                       Matrix.CreateScale(_scale, _scale) *
                       Matrix.CreateTranslation(-_content.X * _scale, -_content.Y * _scale)))
            {
                foreach (var item in _document.Items)
                {
                    item.Draw(context);
                }
            }
        }
    }
}
