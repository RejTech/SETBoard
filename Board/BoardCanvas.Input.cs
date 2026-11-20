using Avalonia;
using Avalonia.Input;

namespace SETBoard.Board;

public sealed partial class BoardCanvas
{
    private enum Interaction
    {
        None,
        Drawing,
        Shaping,
        Erasing,
        Moving,
        Marquee,
        Panning,
        Gesture,
        RotatingSolid,
        Resizing,
    }

    private Interaction _interaction;
    private int _activePointerId;
    private readonly HashSet<BoardItem> _selection = new();
    private readonly List<BoardItem> _eraseRemoved = new();
    private readonly List<BoardItem> _eraseAdded = new();
    private InkStroke? _liveStroke;
    private ShapeItem? _previewShape;
    private Point _shapeStart;
    private Point _moveLastWorld;
    private Vector _moveTotal;
    private Point _marqueeStart;
    private Point _marqueeCurrent;
    private readonly Dictionary<int, Point> _touchPoints = new();
    private readonly HashSet<int> _penPointers = new();
    private double _gestureStartDistance = 1;
    private Point _gestureStartCenter;
    private double _gestureStartZoom = 1;
    private Vector _gestureStartOffset;
    private SolidItem? _rotateTarget;
    private Point _rotateStart;
    private double _rotateStartYaw;
    private double _rotateStartPitch;
    private BoardItem? _resizeTarget;
    private ItemSizeState _resizeStart;
    private Point _resizeOrigin;
    private Point _resizeStartHandle;
    private Rect _resizeStartRect;
    private Point _lastScreenPoint;
    /// <summary>橡皮光标所在的屏幕坐标，null 表示不显示擦除范围框。</summary>
    private Point? _eraserScreen;
    private long _lastClickTick;
    private Point _lastClickPoint;
    private int _lastClickPointer = -1;

    /// <summary>选择工具下双击画布（世界坐标），用于打开函数图像的参数面板。</summary>
    public event EventHandler<Point>? DoubleClicked;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _viewAnimationToken++;
        EnsureCamera();
        Focus();

        var current = e.GetCurrentPoint(this);
        var screen = current.Position;
        var world = ToWorld(screen);
        var pointerType = e.Pointer.Type;

        // 双击检测：触屏的双击容易误触，只认笔和鼠标
        if (Tool == BoardTool.Select && pointerType != PointerType.Touch
            && !IsResizeHandleHit(screen))
        {
            long now = Environment.TickCount64;
            if (now - _lastClickTick <= 400 && _lastClickPointer == e.Pointer.Id
                && Math.Abs(screen.X - _lastClickPoint.X) <= 14
                && Math.Abs(screen.Y - _lastClickPoint.Y) <= 14)
            {
                _lastClickTick = 0;
                _lastClickPointer = -1;
                DoubleClicked?.Invoke(this, world);
            }
            else
            {
                _lastClickTick = now;
                _lastClickPointer = e.Pointer.Id;
                _lastClickPoint = screen;
            }
        }

        // 触控笔优先：笔尖按下时忽略手掌/手指触摸
        if (pointerType == PointerType.Touch)
        {
            // 直读通道已经检测到笔尖按下，触摸直接丢弃，避免再走一遍手势判定
            if (IsRawPenActive)
            {
                e.Handled = true;
                return;
            }

            if (_penPointers.Count > 0)
            {
                e.Handled = true;
                return;
            }

            _touchPoints[e.Pointer.Id] = screen;
            if (_touchPoints.Count == 2)
            {
                CancelActiveInteraction();
                BeginGesture();
                e.Handled = true;
                return;
            }

            if (_touchPoints.Count > 2)
            {
                e.Handled = true;
                return;
            }
        }
        else if (pointerType == PointerType.Pen)
        {
            _penPointers.Add(e.Pointer.Id);
            // 笔尖落下时立即接管：丢弃正在进行的手指书写（防手掌误触）
            if (_interaction != Interaction.None && _activePointerId != 0
                && _touchPoints.ContainsKey(_activePointerId))
            {
                CancelActiveInteraction();
            }
        }

        if (_interaction != Interaction.None) return;
        // 悬浮工具栏区域不接收落笔，避免笔迹从半透明面板下透出
        if (IsInkExcluded(screen)) return;

        bool forceErase = current.Properties.IsRightButtonPressed;
        if (current.Properties.IsMiddleButtonPressed || Tool == BoardTool.Pan && !forceErase)
        {
            _interaction = Interaction.Panning;
            _lastScreenPoint = screen;
        }
        else if (Tool == BoardTool.Eraser || forceErase)
        {
            StartErase(world);
        }
        else
        {
            switch (Tool)
            {
                case BoardTool.Select:
                    StartSelect(world, screen, e.KeyModifiers);
                    break;
                case BoardTool.Pen:
                    StartInk(world, GetPressure(current, pointerType), false);
                    break;
                case BoardTool.Highlighter:
                    StartInk(world, 1.0, true);
                    break;
                case BoardTool.Fill:
                    FillAt(world, screen);
                    break;
                default:
                    StartShape(world);
                    break;
            }
        }

        _activePointerId = e.Pointer.Id;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_eraserScreen is null) return;
        _eraserScreen = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);

        // 橡皮的擦除范围框跟随指针，按下擦除时同样可见
        if (Tool == BoardTool.Eraser || _interaction == Interaction.Erasing)
        {
            _eraserScreen = screen;
            InvalidateVisual();
        }

        if (e.Pointer.Type == PointerType.Touch && _touchPoints.ContainsKey(e.Pointer.Id))
        {
            _touchPoints[e.Pointer.Id] = screen;
            if (_interaction == Interaction.Gesture)
            {
                UpdateGesture();
                e.Handled = true;
                return;
            }
        }

        if (_interaction is Interaction.None or Interaction.Gesture) return;
        if (e.Pointer.Id != _activePointerId) return;

        var world = ToWorld(screen);
        switch (_interaction)
        {
            case Interaction.Drawing:
                // 笔迹滑进工具栏区域时不再追加点，笔画停在边界
                if (IsInkExcluded(screen)) break;
                _liveStroke?.AddPoint(world, GetPressure(e.GetCurrentPoint(this), e.Pointer.Type), 1.0 / _zoom);
                InvalidateVisual();
                break;

            case Interaction.Shaping:
                if (_previewShape is not null)
                {
                    _previewShape.X2 = world.X;
                    _previewShape.Y2 = world.Y;
                    InvalidateVisual();
                }

                break;

            case Interaction.Erasing:
                EraseAt(world);
                break;

            case Interaction.Moving:
            {
                var delta = world - _moveLastWorld;
                if (Math.Abs(delta.X) > 0.0001 || Math.Abs(delta.Y) > 0.0001)
                {
                    foreach (var item in _selection) item.Translate(delta);
                    _moveTotal += delta;
                    _moveLastWorld = world;
                    InvalidateVisual();
                }

                break;
            }

            case Interaction.Marquee:
                _marqueeCurrent = screen;
                InvalidateVisual();
                break;

            case Interaction.RotatingSolid:
            {
                if (_rotateTarget is null) break;
                var delta = screen - _rotateStart;
                _rotateTarget.Yaw = _rotateStartYaw + delta.X * 0.55;
                _rotateTarget.Pitch = _rotateStartPitch + delta.Y * 0.45;
                InvalidateVisual();
                break;
            }

            case Interaction.Resizing:
                UpdateResize(screen);
                break;

            case Interaction.Panning:
            {
                var delta = screen - _lastScreenPoint;
                _offset += new Vector(delta.X, delta.Y);
                _lastScreenPoint = screen;
                InvalidateVisual();
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pointerType = e.Pointer.Type;

        if (pointerType == PointerType.Pen)
        {
            _penPointers.Remove(e.Pointer.Id);
        }

        if (pointerType == PointerType.Touch && _touchPoints.Remove(e.Pointer.Id)
            && _interaction == Interaction.Gesture)
        {
            if (_touchPoints.Count < 2)
            {
                _interaction = Interaction.None;
                _activePointerId = 0;
                e.Pointer.Capture(null);
            }

            e.Handled = true;
            return;
        }

        // 直读通道的抬笔万一丢了（窗口句柄重建等），用 Avalonia 的抬笔兜底收笔
        if (_interaction == Interaction.Drawing && _activePointerId >= RawPointerIdBase)
        {
            FinishInteraction();
            e.Handled = true;
            return;
        }

        if (e.Pointer.Id != _activePointerId)
        {
            e.Pointer.Capture(null);
            return;
        }

        FinishInteraction();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (e.Pointer.Type == PointerType.Pen)
        {
            _penPointers.Remove(e.Pointer.Id);
        }

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touchPoints.Remove(e.Pointer.Id);
            if (_interaction == Interaction.Gesture)
            {
                _interaction = Interaction.None;
                return;
            }
        }

        if (e.Pointer.Id == _activePointerId && _interaction != Interaction.None)
        {
            FinishInteraction();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = Math.Pow(1.12, e.Delta.Y);
        if (Math.Abs(factor - 1) < 0.0001) return;
        ZoomBy(factor, e.GetPosition(this));
        e.Handled = true;
    }

    private static double GetPressure(PointerPoint point, PointerType type)
    {
        if (type != PointerType.Pen) return 1.0;
        return Math.Clamp(point.Properties.Pressure, 0.05, 1.0);
    }

    private void StartInk(Point world, double pressure, bool highlighter)
    {
        // 粗细按“屏幕上的粗细”取值：当前缩放下开始书写时多粗，落到画面上就是多粗
        double screenWidth = highlighter ? InkWidth * 2.4 : InkWidth;

        _liveStroke = new InkStroke
        {
            Color = InkColor,
            Width = screenWidth / _zoom,
            Highlighter = highlighter,
        };
        _liveStroke.AddPoint(world, pressure, 1.0 / _zoom);
        _interaction = Interaction.Drawing;
    }

    private void StartShape(Point world)
    {
        var kind = Tool switch
        {
            BoardTool.Line => ShapeKind.Line,
            BoardTool.Arrow => ShapeKind.Arrow,
            BoardTool.Rectangle => ShapeKind.Rectangle,
            BoardTool.Ellipse => ShapeKind.Ellipse,
            _ => ShapeKind.Triangle,
        };

        _shapeStart = world;
        _previewShape = new ShapeItem
        {
            Kind = kind,
            X1 = world.X,
            Y1 = world.Y,
            X2 = world.X,
            Y2 = world.Y,
            Color = InkColor,
            Width = InkWidth,
        };
        _interaction = Interaction.Shaping;
    }

    private void StartSelect(Point world, Point screen, KeyModifiers modifiers)
    {
        if (_selection.Count == 1 && _selection.First() is { } selected
            && GetResizeHandle(selected) is { } handle
            && Distance(screen, handle) <= 16)
        {
            _resizeTarget = selected;
            _resizeStart = ItemSizeState.Capture(selected);
            _resizeStartRect = selected is ShapeItem shape ? shape.Rect : selected.Bounds;
            _resizeStartHandle = handle;
            _resizeOrigin = selected switch
            {
                SolidItem solidToResize => ToScreen(new Point(solidToResize.CenterX, solidToResize.CenterY)),
                ShapeItem => ToScreen(_resizeStartRect.TopLeft),
                _ => ToScreen(new Point(_resizeStart.X, _resizeStart.Y)),
            };
            _interaction = Interaction.Resizing;
            _lastClickTick = 0;
            return;
        }
        bool additive = (modifiers & KeyModifiers.Control) != 0;
        var hit = PickItem(world, 8 / _zoom);

        // 已选中的立体图形再拖动就是三维旋转，未选中时拖动仍然是移动
        if (hit is SolidItem solid && _selection.Contains(solid))
        {
            _interaction = Interaction.RotatingSolid;
            _rotateTarget = solid;
            _rotateStart = screen;
            _rotateStartYaw = solid.Yaw;
            _rotateStartPitch = solid.Pitch;
            return;
        }

        if (hit is not null)
        {
            if (!_selection.Contains(hit))
            {
                if (!additive) _selection.Clear();
                _selection.Add(hit);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            _moveLastWorld = world;
            _moveTotal = default;
            _interaction = Interaction.Moving;
            return;
        }

        if (!additive)
        {
            if (_selection.Count > 0)
            {
                _selection.Clear();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        _marqueeStart = screen;
        _marqueeCurrent = screen;
        _interaction = Interaction.Marquee;
    }

    private bool IsResizeHandleHit(Point screen) =>
        _selection.Count == 1 && GetResizeHandle(_selection.First()) is { } handle
        && Distance(screen, handle) <= 16;

    private void UpdateResize(Point screen)
    {
        if (_resizeTarget is null) return;
        double initial = Distance(_resizeStartHandle, _resizeOrigin);
        if (initial < 1) return;
        double factor = Math.Clamp(Distance(screen, _resizeOrigin) / initial, 0.05, 50);
        if ((screen.X - _resizeOrigin.X) * (_resizeStartHandle.X - _resizeOrigin.X)
            + (screen.Y - _resizeOrigin.Y) * (_resizeStartHandle.Y - _resizeOrigin.Y) <= 0) factor = 0.05;
        double startWidth = _resizeTarget is ShapeItem ? Math.Max(1, _resizeStartRect.Width) : _resizeStart.Width;
        double startHeight = _resizeTarget is ShapeItem ? Math.Max(1, _resizeStartRect.Height) : _resizeStart.Height;
        double minimum = _resizeTarget switch
        {
            SolidItem => Math.Max(20 / startWidth, 20 / startHeight),
            FunctionItem => Math.Max(40 / startWidth, 30 / startHeight),
            ShapeItem => Math.Max(4 / startWidth, 4 / startHeight),
            _ => Math.Max(20 / startWidth, 20 / startHeight),
        };
        factor = Math.Max(minimum, factor);
        if (_resizeTarget is ShapeItem shape)
        {
            shape.X1 = _resizeStartRect.Left + (_resizeStart.X - _resizeStartRect.Left) * factor;
            shape.Y1 = _resizeStartRect.Top + (_resizeStart.Y - _resizeStartRect.Top) * factor;
            shape.X2 = _resizeStartRect.Left + (_resizeStart.Width - _resizeStartRect.Left) * factor;
            shape.Y2 = _resizeStartRect.Top + (_resizeStart.Height - _resizeStartRect.Top) * factor;
            InvalidateVisual();
            return;
        }
        (_resizeStart with
        {
            Width = _resizeStart.Width * factor,
            Height = _resizeStart.Height * factor,
            StrokeWidth = _resizeTarget is FunctionItem
                ? _resizeStart.StrokeWidth * factor
                : _resizeStart.StrokeWidth,
        }).Apply(_resizeTarget);
        InvalidateVisual();
    }

    private void StartErase(Point world)
    {
        _eraseRemoved.Clear();
        _eraseAdded.Clear();
        _interaction = Interaction.Erasing;
        EraseAt(world);
    }

    private void EraseAt(Point world)
    {
        double radius = _eraserRadiusScreen / _zoom;
        bool changed = false;

        for (int i = _document.Items.Count - 1; i >= 0; i--)
        {
            var item = _document.Items[i];
            if (item is InkStroke stroke)
            {
                if (!stroke.HitTest(world, radius)) continue;
                var pieces = stroke.EraseAt(world, radius);
                if (pieces.Count == 1 && pieces[0].Points.Count == stroke.Points.Count) continue;

                _document.Items.RemoveAt(i);
                _selection.Remove(stroke);
                _eraseRemoved.Add(stroke);
                foreach (var piece in pieces)
                {
                    _document.Items.Add(piece);
                    _eraseAdded.Add(piece);
                }

                changed = true;
            }
            else if (item.HitTest(world, radius))
            {
                _document.Items.RemoveAt(i);
                _selection.Remove(item);
                _eraseRemoved.Add(item);
                changed = true;
            }
        }

        if (changed) InvalidateVisual();
    }

    private void FinishInteraction()
    {
        switch (_interaction)
        {
            case Interaction.Drawing:
                if (_liveStroke is { Points.Count: > 0 })
                {
                    _document.Items.Add(_liveStroke);
                    History.Push(new ChangeCommand(Array.Empty<BoardItem>(), new[] { _liveStroke }));
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }

                _liveStroke = null;
                break;

            case Interaction.Shaping:
                if (_previewShape is not null)
                {
                    var bounds = _previewShape.Rect;
                    bool bigEnough = bounds.Width * _zoom > 3 || bounds.Height * _zoom > 3;
                    if (bigEnough)
                    {
                        _document.Items.Add(_previewShape);
                        History.Push(new ChangeCommand(Array.Empty<BoardItem>(), new[] { _previewShape }));
                        DocumentChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                _previewShape = null;
                break;

            case Interaction.Erasing:
                if (_eraseRemoved.Count > 0 || _eraseAdded.Count > 0)
                {
                    History.Push(new ChangeCommand(_eraseRemoved, _eraseAdded));
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }

                _eraseRemoved.Clear();
                _eraseAdded.Clear();
                break;

            case Interaction.Moving:
                if (_moveTotal.Length > 0.3)
                {
                    History.Push(new MoveItemsCommand(_selection, _moveTotal));
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }

                _moveTotal = default;
                break;

            case Interaction.RotatingSolid:
                if (_rotateTarget is not null
                    && (Math.Abs(_rotateTarget.Yaw - _rotateStartYaw) > 0.01
                        || Math.Abs(_rotateTarget.Pitch - _rotateStartPitch) > 0.01))
                {
                    History.Push(new RotateSolidCommand(_rotateTarget,
                        _rotateStartYaw, _rotateStartPitch, _rotateTarget.Yaw, _rotateTarget.Pitch));
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }

                _rotateTarget = null;
                break;

            case Interaction.Resizing:
                if (_resizeTarget is not null)
                {
                    var after = ItemSizeState.Capture(_resizeTarget);
                    if (after != _resizeStart)
                    {
                        History.Push(new ResizeItemCommand(_resizeTarget, _resizeStart, after));
                        DocumentChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                _resizeTarget = null;
                break;

            case Interaction.Marquee:
            {
                var rect = new Rect(_marqueeStart, _marqueeCurrent).Normalize();
                if (rect.Width > 3 && rect.Height > 3)
                {
                    var worldRect = new Rect(ToWorld(rect.TopLeft), ToWorld(rect.BottomRight)).Normalize();
                    foreach (var item in _document.Items)
                    {
                        if (item.Bounds.Intersects(worldRect))
                        {
                            _selection.Add(item);
                        }
                    }

                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
            }
        }

        _interaction = Interaction.None;
        _activePointerId = 0;
        InvalidateVisual();
    }

    private void CancelActiveInteraction()
    {
        switch (_interaction)
        {
            case Interaction.Erasing:
                if (_eraseRemoved.Count > 0 || _eraseAdded.Count > 0)
                {
                    History.Push(new ChangeCommand(_eraseRemoved, _eraseAdded));
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }

                _eraseRemoved.Clear();
                _eraseAdded.Clear();
                break;

            case Interaction.Moving:
                if (_moveTotal.Length > 0.3)
                {
                    History.Push(new MoveItemsCommand(_selection, _moveTotal));
                    DocumentChanged?.Invoke(this, EventArgs.Empty);
                }

                _moveTotal = default;
                break;
            case Interaction.Resizing:
                if (_resizeTarget is not null)
                {
                    var after = ItemSizeState.Capture(_resizeTarget);
                    if (after != _resizeStart)
                    {
                        History.Push(new ResizeItemCommand(_resizeTarget, _resizeStart, after));
                        DocumentChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                _resizeTarget = null;
                break;
        }

        _liveStroke = null;
        _previewShape = null;
        _rotateTarget = null;
        _interaction = Interaction.None;
        _activePointerId = 0;
        InvalidateVisual();
    }

    private void BeginGesture()
    {
        if (_touchPoints.Count < 2) return;
        var points = _touchPoints.Values.ToList();
        _gestureStartCenter = Midpoint(points[0], points[1]);
        _gestureStartDistance = Math.Max(1, Distance(points[0], points[1]));
        _gestureStartZoom = _zoom;
        _gestureStartOffset = _offset;
        _interaction = Interaction.Gesture;
        _activePointerId = 0;
    }

    private void UpdateGesture()
    {
        if (_touchPoints.Count < 2) return;
        var points = _touchPoints.Values.Take(2).ToList();
        var center = Midpoint(points[0], points[1]);
        double distance = Math.Max(1, Distance(points[0], points[1]));
        double centerX = (_gestureStartCenter.X - _gestureStartOffset.X) / _gestureStartZoom;
        double centerY = (_gestureStartCenter.Y - _gestureStartOffset.Y) / _gestureStartZoom;
        double zoom = Math.Clamp(_gestureStartZoom * distance / _gestureStartDistance, MinZoom, MaxZoom);

        _zoom = zoom;
        _offset = new Vector(center.X - centerX * zoom, center.Y - centerY * zoom);
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Distance(Point a, Point b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
