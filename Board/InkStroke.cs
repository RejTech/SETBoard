using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace SETBoard.Board;

public struct InkPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Pressure { get; set; }

    public InkPoint(double x, double y, double pressure)
    {
        X = x;
        Y = y;
        Pressure = pressure;
    }

    [JsonIgnore] public Point Position => new(X, Y);
}

/// <summary>一笔墨迹。几何在被修改后按需重建。</summary>
public sealed class InkStroke : BoardItem
{
    private List<InkPoint> _points = new();
    private List<InkPoint>? _renderPoints;
    private StreamGeometry? _geometry;
    private Rect _bounds;
    private IBrush? _brush;
    private IPen? _highlighterPen;

    public List<InkPoint> Points
    {
        get => _points;
        set
        {
            _points = value ?? new List<InkPoint>();
            Invalidate();
        }
    }

    private uint _color = 0xFF1A1A1A;
    private double _width = 4;
    private bool _highlighter;

    /// <summary>ARGB 颜色。</summary>
    public uint Color
    {
        get => _color;
        set
        {
            _color = value;
            _brush = null;
            _highlighterPen = null;
        }
    }

    public double Width
    {
        get => _width;
        set { _width = value; Invalidate(); }
    }

    public bool Highlighter
    {
        get => _highlighter;
        set { _highlighter = value; Invalidate(); }
    }

    [JsonIgnore] public override Rect Bounds => _bounds;

    public void AddPoint(Point position, double pressure, double minDistance)
    {
        double p = Math.Clamp(pressure, 0.05, 1.0);
        if (_points.Count > 0)
        {
            var last = _points[^1];
            if (Math.Abs(last.X - position.X) < minDistance && Math.Abs(last.Y - position.Y) < minDistance)
            {
                // 亚像素移动也要让笔尖跟着手指走，否则笔画会“粘”在上一采样点上
                _points[^1] = new InkPoint(position.X, position.Y, (last.Pressure + p) * 0.5);
                _geometry = null;
                _renderPoints = null;

                double tipHalf = Width * (Highlighter ? 1.5 : 0.7);
                _bounds = _bounds.Union(new Rect(position.X - tipHalf, position.Y - tipHalf,
                    tipHalf * 2, tipHalf * 2));
                return;
            }
        }

        _points.Add(new InkPoint(position.X, position.Y, p));
        _geometry = null;
        _renderPoints = null;

        double halfWidth = Width * (Highlighter ? 1.5 : 0.7);
        var pointBounds = new Rect(position.X - halfWidth, position.Y - halfWidth,
            halfWidth * 2, halfWidth * 2);
        _bounds = _points.Count == 1 ? pointBounds : _bounds.Union(pointBounds);
    }

    public override void Invalidate()
    {
        _geometry = null;
        _renderPoints = null;
        _brush = null;
        _highlighterPen = null;
        RecalculateBounds();
    }

    private void RecalculateBounds()
    {
        if (_points.Count == 0)
        {
            _bounds = default;
            return;
        }

        double maxHalfWidth = Width * (Highlighter ? 1.5 : 0.7);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in _points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        _bounds = new Rect(minX - maxHalfWidth, minY - maxHalfWidth,
            maxX - minX + maxHalfWidth * 2, maxY - minY + maxHalfWidth * 2);
    }

    private IBrush GetBrush()
    {
        if (_brush is null)
        {
            byte a = (byte)(Color >> 24);
            byte r = (byte)(Color >> 16);
            byte g = (byte)(Color >> 8);
            byte b = (byte)Color;
            double opacity = Highlighter ? 0.32 : 1.0;
            _brush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(a, r, g, b), opacity).ToImmutable();
        }

        return _brush;
    }

    public override void Draw(DrawingContext context)
    {
        if (_points.Count == 0) return;

        if (_points.Count < 2)
        {
            var p = _points[0].Position;
            double radius = Width * (Highlighter ? 1.5 : 0.5);
            context.DrawEllipse(GetBrush(), null, p, radius, radius);
            return;
        }

        _geometry ??= Highlighter ? BuildPolyline() : BuildGeometry();
        if (Highlighter)
        {
            _highlighterPen ??= new Pen(GetBrush(), Width * 3)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            context.DrawGeometry(null, _highlighterPen, _geometry);
        }
        else
        {
            context.DrawGeometry(GetBrush(), null, _geometry);
        }
    }

    /// <summary>荧光笔使用等宽描边折线，避免宽带状几何自交产生的锯齿空洞。</summary>
    private StreamGeometry BuildPolyline()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var anchors = GetRenderPoints();
            var widths = new double[anchors.Count];
            for (int i = 0; i < widths.Length; i++) widths[i] = Width * 0.5;

            var (points, _) = Resample(anchors, widths);
            if (points.Count == 0) return geometry;

            ctx.BeginFigure(points[0], false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i]);
            }

            ctx.EndFigure(false);
        }

        return geometry;
    }

    /// <summary>
    /// 用「圆头 + 连接片」拼出笔迹：每个采样点画一个圆，相邻点之间补一个四边形。
    /// 所有子图形同向闭合，NonZero 填充下重叠只会叠加不会抵消，
    /// 因此急折返、自交、连笔都不会出现镂空或断线。
    /// </summary>
    private StreamGeometry BuildGeometry()
    {
        var anchors = GetRenderPoints();
        var anchorWidths = ComputeHalfWidths(anchors);

        // 采样点之间用曲线连接并加密，否则触屏的稀疏采样会写出一节节直线
        var (points, halfWidths) = Resample(anchors, anchorWidths);
        int count = points.Count;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(FillRule.NonZero);

            for (int i = 0; i < count; i++)
            {
                AddDisc(ctx, points[i], halfWidths[i]);
            }

            for (int i = 0; i < count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var direction = Direction(a, b);
                if (direction.Length < 0.0001) continue;

                // 圆点已足够密时无需补片，减少几何量
                if (Distance(a, b) <= Math.Min(halfWidths[i], halfWidths[i + 1]) * 0.6) continue;

                var normal = new Vector(-direction.Y, direction.X);
                var quad = new[]
                {
                    a + normal * halfWidths[i],
                    b + normal * halfWidths[i + 1],
                    b - normal * halfWidths[i + 1],
                    a - normal * halfWidths[i],
                };
                AddClosedPolygon(ctx, quad);
            }
        }

        return geometry;
    }

    /// <summary>
    /// 把稀疏的采样点连成光滑中心线：向心 Catmull-Rom（alpha=0.5）插值，
    /// 转角大的地方把控制点收回端点，避免笔画在拐角处鼓包过冲。
    /// 采样步长随笔宽自适应，粗笔稀一点、细笔密一点。
    /// </summary>
    private static (List<Point> Points, List<double> Widths) Resample(
        List<InkPoint> src, double[] srcWidths)
    {
        int count = src.Count;
        var points = new List<Point>(count * 2);
        var widths = new List<double>(count * 2);
        if (count == 0) return (points, widths);

        points.Add(src[0].Position);
        widths.Add(srcWidths[0]);
        if (count == 1) return (points, widths);

        double total = 0;
        for (int i = 1; i < count; i++) total += Distance(src[i - 1].Position, src[i].Position);

        // 限制总点数，超长笔画自动放稀，避免每帧重建几何的代价失控
        double minStep = Math.Max(total / 4000.0, 0.6);

        for (int i = 0; i < count - 1; i++)
        {
            var p1 = src[i].Position;
            var p2 = src[i + 1].Position;
            double w1 = srcWidths[i];
            double w2 = srcWidths[i + 1];
            double chord = Distance(p1, p2);

            if (chord < 0.0001)
            {
                points.Add(p2);
                widths.Add(w2);
                continue;
            }

            // 拐点处收紧控制点，保持转角清晰且不过冲
            var p0 = p1;
            if (i > 0 && TurnDegrees(src[i - 1].Position, p1, p2) <= 60)
            {
                p0 = src[i - 1].Position;
            }

            var p3 = p2;
            if (i + 2 < count && TurnDegrees(p1, p2, src[i + 2].Position) <= 60)
            {
                p3 = src[i + 2].Position;
            }

            double step = Math.Max(minStep, Math.Min(Math.Min(w1, w2) * 0.55, 3.0));
            int steps = Math.Max(1, (int)Math.Ceiling(chord / step));

            double t0 = 0;
            double t1 = t0 + Math.Sqrt(Distance(p0, p1));
            double t2 = t1 + Math.Sqrt(chord);
            double t3 = t2 + Math.Sqrt(Distance(p2, p3));
            if (t1 <= t0) t1 = t0 + 0.0001;
            if (t2 <= t1) t2 = t1 + 0.0001;
            if (t3 <= t2) t3 = t2 + 0.0001;

            for (int s = 1; s <= steps; s++)
            {
                double u = (double)s / steps;
                double t = t1 + (t2 - t1) * u;

                var a1 = Blend(p0, p1, (t1 - t) / (t1 - t0), (t - t0) / (t1 - t0));
                var a2 = Blend(p1, p2, (t2 - t) / (t2 - t1), (t - t1) / (t2 - t1));
                var a3 = Blend(p2, p3, (t3 - t) / (t3 - t2), (t - t2) / (t3 - t2));
                var b1 = Blend(a1, a2, (t2 - t) / (t2 - t0), (t - t0) / (t2 - t0));
                var b2 = Blend(a2, a3, (t3 - t) / (t3 - t1), (t - t1) / (t3 - t1));
                var c = Blend(b1, b2, (t2 - t) / (t2 - t1), (t - t1) / (t2 - t1));

                points.Add(c);
                widths.Add(w1 + (w2 - w1) * u);
            }
        }

        return (points, widths);
    }

    private static Point Blend(Point a, Point b, double wa, double wb)
        => new(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb);

    /// <summary>一个圆形笔头。</summary>
    private static void AddDisc(StreamGeometryContext ctx, Point center, double radius)
    {
        radius = Math.Max(radius, 0.3);
        int steps = radius <= 3 ? 6 : radius <= 8 ? 8 : 10;

        var pts = new Point[steps];
        for (int i = 0; i < steps; i++)
        {
            double angle = Math.PI * 2 * i / steps;
            pts[i] = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        }

        AddClosedPolygon(ctx, pts);
    }

    /// <summary>按统一方向（正面积）闭合多边形，保证 NonZero 叠加语义正确。</summary>
    private static void AddClosedPolygon(StreamGeometryContext ctx, Point[] pts)
    {
        int count = pts.Length;
        if (count < 3) return;

        double area = 0;
        for (int i = 0; i < count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % count];
            area += p.X * q.Y - q.X * p.Y;
        }

        bool reverse = area < 0;
        for (int i = 0; i < count; i++)
        {
            var p = pts[reverse ? count - 1 - i : i];
            if (i == 0)
            {
                ctx.BeginFigure(p, true);
            }
            else
            {
                ctx.LineTo(p);
            }
        }

        ctx.EndFigure(true);
    }

    /// <summary>
    /// 笔锋宽度：压感越重越粗，运笔越快越细，起笔与收笔按弧长渐细。
    /// 参考 Inkeys 的 StrokeWidthEstimator + EnforceCapsuleTangency：
    /// 半径在速度映射后还要做相邻点限速，保证相邻圆头始终存在公共切线，
    /// 否则压感/速度抖动会让边缘出现鼓包和细腰。
    /// </summary>
    private double[] ComputeHalfWidths(List<InkPoint> points)
    {
        int count = points.Count;
        var widths = new double[count];
        double baseHalf = Width * 0.5;

        // 1) 原始半径：压感 × 速度。速度取前后两段距离均值（同 Ink-Canvas 的 GetPointSpeed），比单段更稳。
        for (int i = 0; i < count; i++)
        {
            double pressure = Math.Clamp(points[i].Pressure, 0.05, 1.0);
            double pressureFactor = 0.6 + 0.4 * pressure;

            double spacing = 0;
            if (i > 0 && i < count - 1)
            {
                spacing = (Distance(points[i - 1].Position, points[i].Position)
                           + Distance(points[i].Position, points[i + 1].Position)) * 0.5;
            }
            else if (i > 0)
            {
                spacing = Distance(points[i - 1].Position, points[i].Position);
            }
            else if (count > 1)
            {
                spacing = Distance(points[i].Position, points[i + 1].Position);
            }

            // 慢写粗、快写细：0 → 1.25 倍，达到参考速度 → 0.85 倍
            double speed = SmoothStep(spacing / Math.Max(baseHalf * 2.0, 5.0));
            double speedFactor = 1.25 - 0.4 * speed;

            widths[i] = Math.Max(0.3, baseHalf * pressureFactor * speedFactor);
        }

        // 2) 半径限速 + 轻度平滑，消除单点尖刺（Inkeys 的胶囊公切线约束）
        ClampRadiusChanges(widths, points);
        SmoothRadii(widths);

        // 3) 首尾收锋：长度与笔身半径挂钩，端头最细只到 30%，
        //    粗笔短划不会收成一根发丝（Inkeys 的 1-0.7*SmoothStep 规则）。
        double length = 0;
        for (int i = 1; i < count; i++) length += Distance(points[i - 1].Position, points[i].Position);
        if (length >= MaxRadius(widths) * 8) ApplyTaper(widths, points, fromStart: true);
        ApplyTaper(widths, points, fromStart: false);

        return widths;
    }

    /// <summary>相邻点半径变化按距离限速，前后各走一遍，保证圆头之间能生成公共切线。</summary>
    private static void ClampRadiusChanges(double[] widths, List<InkPoint> points)
    {
        const double slope = 0.35;
        for (int i = 1; i < widths.Length; i++)
        {
            double maxDelta = Distance(points[i - 1].Position, points[i].Position) * slope;
            widths[i] = widths[i - 1] + Math.Clamp(widths[i] - widths[i - 1], -maxDelta, maxDelta);
        }

        for (int i = widths.Length - 2; i >= 0; i--)
        {
            double maxDelta = Distance(points[i].Position, points[i + 1].Position) * slope;
            widths[i] = widths[i + 1] + Math.Clamp(widths[i] - widths[i + 1], -maxDelta, maxDelta);
        }
    }

    /// <summary>3 抽头平滑半径曲线，压感噪声不会形成锯齿状边缘。</summary>
    private static void SmoothRadii(double[] widths)
    {
        if (widths.Length < 3) return;
        var copy = (double[])widths.Clone();
        for (int i = 1; i < widths.Length - 1; i++)
        {
            widths[i] = copy[i - 1] * 0.25 + copy[i] * 0.5 + copy[i + 1] * 0.25;
        }
    }

    /// <summary>从一端按弧长收锋：端头比例由锋长与笔身半径之比决定，整段用 SmoothStep 过渡。</summary>
    private static void ApplyTaper(double[] widths, List<InkPoint> points, bool fromStart)
    {
        int count = widths.Length;
        if (count < 2) return;

        double reference = MaxRadius(widths);
        if (reference <= 0.3) return;

        double length = 0;
        for (int i = 1; i < count; i++) length += Distance(points[i - 1].Position, points[i].Position);

        double taper = Math.Min(length * (fromStart ? 0.3 : 0.45), reference * (fromStart ? 6 : 10));
        if (taper <= 0.5) return;

        double tipRatio = 1 - 0.7 * SmoothStep(taper / (4 * reference));
        double done = 0;
        for (int k = 0; k < count && done < taper; k++)
        {
            int i = fromStart ? k : count - 1 - k;
            int previous = fromStart ? k - 1 : count - k;
            if (k > 0) done += Distance(points[previous].Position, points[i].Position);

            double t = SmoothStep(done / taper);
            widths[i] *= tipRatio + (1 - tipRatio) * t;
        }
    }

    private static double MaxRadius(double[] widths)
    {
        double max = 0;
        foreach (var w in widths)
        {
            if (w > max) max = w;
        }

        return max;
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>绘制时使用的点列：先去重再轻度平滑，抑制采样抖动造成的尖刺。</summary>
    private List<InkPoint> GetRenderPoints()
    {
        if (_renderPoints is not null) return _renderPoints;

        double minStep = Math.Max(0.5, Width * 0.1);
        var filtered = new List<InkPoint>(_points.Count);
        foreach (var p in _points)
        {
            if (filtered.Count > 0)
            {
                var last = filtered[^1];
                if (Distance(last.Position, p.Position) < minStep)
                {
                    filtered[^1] = new InkPoint(p.X, p.Y, (last.Pressure + p.Pressure) * 0.5);
                    continue;
                }
            }

            filtered.Add(p);
        }

        int count = filtered.Count;
        if (count == 1 && _points.Count >= 2)
        {
            filtered.Add(_points[^1]);
            count = 2;
        }

        if (count >= 3)
        {
            var smoothed = new List<InkPoint>(count) { filtered[0] };
            for (int i = 1; i < count - 1; i++)
            {
                var a = filtered[i - 1];
                var b = filtered[i];
                var c = filtered[i + 1];

                // 只平滑轻微抖动；明显的转折（写字、折线）原样保留，避免削掉棱角
                if (TurnDegrees(a.Position, b.Position, c.Position) > 35)
                {
                    smoothed.Add(b);
                    continue;
                }

                smoothed.Add(new InkPoint(
                    (a.X + b.X * 2 + c.X) * 0.25,
                    (a.Y + b.Y * 2 + c.Y) * 0.25,
                    (a.Pressure + b.Pressure * 2 + c.Pressure) * 0.25));
            }

            smoothed.Add(filtered[count - 1]);
            filtered = smoothed;
        }

        _renderPoints = filtered;
        return filtered;
    }

    /// <summary>a→b→c 的转角（度），0 表示完全直行。</summary>
    private static double TurnDegrees(Point a, Point b, Point c)
    {
        var u = new Vector(b.X - a.X, b.Y - a.Y);
        var v = new Vector(c.X - b.X, c.Y - b.Y);
        if (u.Length < 0.0001 || v.Length < 0.0001) return 0;

        double cos = Vector.Dot(u.Normalize(), v.Normalize());
        cos = Math.Clamp(cos, -1, 1);
        return Math.Acos(cos) * 180 / Math.PI;
    }

    private static Vector Direction(Point from, Point to)
    {
        var v = new Vector(to.X - from.X, to.Y - from.Y);
        return v.Length < 0.0001 ? default : v.Normalize();
    }

    public override bool HitTest(Point point, double tolerance)
    {
        if (_points.Count == 0) return false;
        double limit = Width * (Highlighter ? 1.5 : 0.75) + tolerance;

        if (_points.Count == 1)
        {
            return Distance(point, _points[0].Position) <= limit;
        }

        for (int i = 1; i < _points.Count; i++)
        {
            if (DistanceToSegment(point, _points[i - 1].Position, _points[i].Position) <= limit)
            {
                return true;
            }
        }

        return false;
    }

    internal static double DistanceToSegment(Point p, Point a, Point b)
    {
        double abX = b.X - a.X;
        double abY = b.Y - a.Y;
        double lengthSquared = abX * abX + abY * abY;
        if (lengthSquared < 0.000001) return Distance(p, a);

        double t = ((p.X - a.X) * abX + (p.Y - a.Y) * abY) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        var projection = new Point(a.X + abX * t, a.Y + abY * t);
        return Distance(p, projection);
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override void Translate(Vector delta)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            _points[i] = new InkPoint(p.X + delta.X, p.Y + delta.Y, p.Pressure);
        }

        _bounds = _bounds.Translate(delta);
        _geometry = null;
        _renderPoints = null;
    }

    /// <summary>橡皮擦局部擦除：返回擦除后剩余的笔画。</summary>
    public List<InkStroke> EraseAt(Point center, double radius)
    {
        double limit = radius + Width * (Highlighter ? 1.5 : 0.5);
        var segments = new List<InkStroke>();
        var current = new List<InkPoint>();

        foreach (var p in _points)
        {
            if (Distance(p.Position, center) <= limit)
            {
                if (current.Count >= 2)
                {
                    segments.Add(CreateSegment(current));
                }

                current = new List<InkPoint>();
            }
            else
            {
                current.Add(p);
            }
        }

        if (current.Count >= 2)
        {
            segments.Add(CreateSegment(current));
        }

        return segments;
    }

    private InkStroke CreateSegment(List<InkPoint> points)
    {
        return new InkStroke
        {
            Points = points,
            Color = Color,
            Width = Width,
            Highlighter = Highlighter,
        };
    }
}
