using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace SETBoard.Board;

public enum ShapeKind
{
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Triangle,
}

/// <summary>直线/箭头/矩形/椭圆/三角形。</summary>
public sealed class ShapeItem : BoardItem
{
    private double _x1, _y1, _x2, _y2;
    private uint _color = 0xFF1A1A1A;
    private double _width = 4;
    private ShapeKind _kind;
    private IPen? _pen;
    private IBrush? _brush;

    public ShapeKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    public double X1 { get => _x1; set { _x1 = value; Invalidate(); } }
    public double Y1 { get => _y1; set { _y1 = value; Invalidate(); } }
    public double X2 { get => _x2; set { _x2 = value; Invalidate(); } }
    public double Y2 { get => _y2; set { _y2 = value; Invalidate(); } }

    public uint Color
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    public double Width
    {
        get => _width;
        set { _width = value; Invalidate(); }
    }

    [JsonIgnore] public Point Start => new(_x1, _y1);
    [JsonIgnore] public Point End => new(_x2, _y2);

    [JsonIgnore]
    public Rect Rect
    {
        get
        {
            double left = Math.Min(_x1, _x2);
            double top = Math.Min(_y1, _y2);
            return new Rect(left, top, Math.Abs(_x2 - _x1), Math.Abs(_y2 - _y1));
        }
    }

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            double padding = _width * 0.5 + 6;
            var r = Rect;
            return new Rect(r.X - padding, r.Y - padding, r.Width + padding * 2, r.Height + padding * 2);
        }
    }

    public override void Invalidate()
    {
        _pen = null;
        _brush = null;
    }

    private IPen GetPen()
    {
        if (_pen is null)
        {
            _brush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
                (byte)(_color >> 24), (byte)(_color >> 16), (byte)(_color >> 8), (byte)_color)).ToImmutable();
            _pen = new Pen(_brush, _width)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }.ToImmutable();
        }

        return _pen;
    }

    private IBrush GetBrush()
    {
        GetPen();
        return _brush!;
    }

    public override void Draw(DrawingContext context)
    {
        var pen = GetPen();
        var rect = Rect;

        switch (_kind)
        {
            case ShapeKind.Line:
                context.DrawLine(pen, Start, End);
                break;

            case ShapeKind.Arrow:
            {
                context.DrawLine(pen, Start, End);
                var direction = new Vector(End.X - Start.X, End.Y - Start.Y);
                if (direction.Length > 0.01)
                {
                    direction = direction.Normalize();
                    var normal = new Vector(-direction.Y, direction.X);
                    double head = Math.Max(12, _width * 3.6);
                    var baseCenter = End - direction * head;
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(End, true);
                        ctx.LineTo(baseCenter + normal * head * 0.42);
                        ctx.LineTo(baseCenter - normal * head * 0.42);
                        ctx.EndFigure(true);
                    }

                    context.DrawGeometry(GetBrush(), null, geometry);
                }

                break;
            }

            case ShapeKind.Rectangle:
                context.DrawRectangle(null, pen, rect);
                break;

            case ShapeKind.Ellipse:
                context.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                    rect.Width / 2, rect.Height / 2);
                break;

            case ShapeKind.Triangle:
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(rect.X + rect.Width / 2, rect.Y), false);
                    ctx.LineTo(new Point(rect.Right, rect.Bottom));
                    ctx.LineTo(new Point(rect.X, rect.Bottom));
                    ctx.EndFigure(true);
                }

                context.DrawGeometry(null, pen, geometry);
                break;
            }
        }
    }

    public override bool HitTest(Point point, double tolerance)
    {
        double padding = tolerance + _width * 0.5;
        if (_kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            return InkStroke.DistanceToSegment(point, Start, End) <= padding + 2;
        }

        var r = Rect;
        var hitBox = new Rect(r.X - padding, r.Y - padding, r.Width + padding * 2, r.Height + padding * 2);
        return hitBox.Contains(point);
    }

    public override void Translate(Vector delta)
    {
        _x1 += delta.X;
        _y1 += delta.Y;
        _x2 += delta.X;
        _y2 += delta.Y;
    }
}
