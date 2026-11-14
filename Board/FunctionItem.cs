using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace SETBoard.Board;

/// <summary>画布上的函数图像：按表达式采样出曲线，带坐标轴。</summary>
public sealed class FunctionItem : BoardItem
{
    private string _expression = "sin(x)";
    private double _parameterA = 1;
    private double _xMin = -10;
    private double _xMax = 10;
    private double _yMin = -4;
    private double _yMax = 4;
    private double _left;
    private double _top;
    private double _plotWidth = 560;
    private double _plotHeight = 360;
    private uint _color = 0xFFFFFFFF;
    private double _width = 3;

    private readonly List<FunctionCurve> _curves = new();
    private IPen? _axisPen;
    private readonly List<Point> _intersections = new();
    private string _intersectionKey = string.Empty;

    /// <summary>函数表达式，自变量为 x，参数为 a。</summary>
    public string Expression
    {
        get => _expression;
        set
        {
            _expression = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>原始 LaTeX 输入，仅用于再次编辑时回填；为空时用 Expression 兜底。</summary>
    public string Latex { get; set; } = string.Empty;

    /// <summary>滑块可调的参数 a。</summary>
    public double ParameterA
    {
        get => _parameterA;
        set
        {
            _parameterA = value;
            Invalidate();
        }
    }

    /// <summary>绘图区左上角（世界坐标）。</summary>
    public double Left
    {
        get => _left;
        set { _left = value; Invalidate(); }
    }

    public double Top
    {
        get => _top;
        set { _top = value; Invalidate(); }
    }

    public double PlotWidth
    {
        get => _plotWidth;
        set { _plotWidth = Math.Max(40, value); Invalidate(); }
    }

    public double PlotHeight
    {
        get => _plotHeight;
        set { _plotHeight = Math.Max(30, value); Invalidate(); }
    }

    /// <summary>横轴取值范围。</summary>
    public double XMin
    {
        get => _xMin;
        set { _xMin = value; Invalidate(); }
    }

    public double XMax
    {
        get => _xMax;
        set { _xMax = value; Invalidate(); }
    }

    /// <summary>纵轴取值范围，插入时自动估算后固定，滑块调节时不跳动。</summary>
    public double YMin
    {
        get => _yMin;
        set { _yMin = value; Invalidate(); }
    }

    public double YMax
    {
        get => _yMax;
        set { _yMax = value; Invalidate(); }
    }

    public uint Color
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    public double Width
    {
        get => _width;
        set { _width = Math.Clamp(value, 0.5, 40); Invalidate(); }
    }

    [JsonIgnore] public Rect PlotArea => new(_left, _top, _plotWidth, _plotHeight);

    [JsonIgnore] public override Rect Bounds
    {
        get
        {
            double pad = _width + 4;
            return new Rect(_left - pad, _top - pad, _plotWidth + pad * 2, _plotHeight + pad * 2);
        }
    }

    [JsonIgnore] public bool ParseFailed => GetCurves().Any(c => c.ParseFailed);

    /// <summary>坐标轴上的曲线，每条曲线有自己的颜色与参数 a。</summary>
    public List<FunctionCurve> Curves
    {
        get => _curves;
        set
        {
            _curves.Clear();
            if (value is not null) _curves.AddRange(value);
            Invalidate();
        }
    }

    /// <summary>取曲线列表；旧存档只存了单个表达式，这里顺便迁移成一条曲线。</summary>
    public IReadOnlyList<FunctionCurve> GetCurves()
    {
        if (_curves.Count == 0 && !string.IsNullOrWhiteSpace(_expression))
        {
            _curves.Add(new FunctionCurve
            {
                Expression = _expression,
                Latex = Latex,
                ParameterA = _parameterA,
                Color = _color,
            });
        }

        return _curves;
    }

    public override void Invalidate()
    {
        _axisPen = null;
        _intersectionKey = string.Empty;
        foreach (var curve in _curves) curve.Invalidate();
    }

    /// <summary>把 x 映射到画布横坐标。</summary>
    public double MapX(double x) =>
        _left + (x - _xMin) / SafeSpan(_xMax - _xMin) * _plotWidth;

    /// <summary>把 y 映射到画布纵坐标（屏幕向下为正）。</summary>
    public double MapY(double y) =>
        _top + _plotHeight - (y - _yMin) / SafeSpan(_yMax - _yMin) * _plotHeight;

    private static double SafeSpan(double span) => Math.Abs(span) < 1e-9 ? 1e-9 : span;

    private IPen GetAxisPen()
    {
        if (_axisPen is null)
        {
            byte a = (byte)((byte)(_color >> 24) * 0.45);
            var brush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
                a, (byte)(_color >> 16), (byte)(_color >> 8), (byte)_color)).ToImmutable();
            _axisPen = new Pen(brush, 1.1).ToImmutable();
        }

        return _axisPen;
    }

    public override void Draw(DrawingContext context)
    {
        var axis = GetAxisPen();

        // 坐标轴（只画落在绘图区内的那一条）
        if (_yMin < 0 && _yMax > 0)
        {
            double y = MapY(0);
            context.DrawLine(axis, new Point(_left, y), new Point(_left + _plotWidth, y));
        }

        if (_xMin < 0 && _xMax > 0)
        {
            double x = MapX(0);
            context.DrawLine(axis, new Point(x, _top), new Point(x, _top + _plotHeight));
        }

        foreach (var curve in GetCurves())
        {
            var pen = curve.GetPen(_width);
            foreach (var segment in curve.GetSegments(_xMin, _xMax, _yMin, _yMax, _left, _top, _plotWidth, _plotHeight))
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(segment[0], false);
                    for (int i = 1; i < segment.Length; i++) ctx.LineTo(segment[i]);
                    ctx.EndFigure(false);
                }

                context.DrawGeometry(null, pen, geometry);
            }
        }

        DrawIntersections(context);
    }

    /// <summary>画出曲线之间的交点：实心圆点 + 一圈淡淡的光晕。</summary>
    private void DrawIntersections(DrawingContext context)
    {
        var points = GetIntersections();
        if (points.Count == 0) return;

        double radius = Math.Clamp(_width * 1.4, 4, 7);
        var fill = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
            (byte)(_color >> 24), (byte)(_color >> 16), (byte)(_color >> 8), (byte)_color)).ToImmutable();
        var halo = new Pen(new SolidColorBrush(Avalonia.Media.Color.FromArgb(
            0x59, (byte)(_color >> 16), (byte)(_color >> 8), (byte)_color)), 1.2).ToImmutable();

        foreach (var point in points)
        {
            var center = new Point(MapX(point.X), MapY(point.Y));
            context.DrawEllipse(fill, null, center, radius, radius);
            context.DrawEllipse(null, halo, center, radius + 1.8, radius + 1.8);
        }
    }

    /// <summary>
    /// 用采样加二分法找所有两两曲线之间的交点，结果按表达式与坐标范围缓存。
    /// </summary>
    private List<Point> GetIntersections()
    {
        var curves = GetCurves();
        if (curves.Count < 2)
        {
            _intersections.Clear();
            _intersectionKey = string.Empty;
            return _intersections;
        }

        var key = new System.Text.StringBuilder();
        key.Append(_xMin).Append('|').Append(_xMax).Append('|').Append(_yMin).Append('|').Append(_yMax);
        foreach (var curve in curves)
        {
            key.Append('|').Append(curve.Expression).Append('@').Append(curve.ParameterA);
        }

        string signature = key.ToString();
        if (signature == _intersectionKey) return _intersections;

        _intersectionKey = signature;
        _intersections.Clear();

        double spanX = _xMax - _xMin;
        double spanY = _yMax - _yMin;
        if (Math.Abs(spanX) < 1e-9 || Math.Abs(spanY) < 1e-9) return _intersections;

        const int samples = 720;
        double minGap = Math.Abs(spanX) / samples * 1.5;
        double bracketLimit = Math.Abs(spanY) * 4;

        for (int i = 0; i < curves.Count; i++)
        {
            for (int j = i + 1; j < curves.Count; j++)
            {
                double? previousX = null;
                double? previousDiff = null;

                for (int s = 0; s <= samples; s++)
                {
                    double x = _xMin + spanX * s / samples;
                    double left = curves[i].Evaluate(x);
                    double right = curves[j].Evaluate(x);
                    if (!double.IsFinite(left) || !double.IsFinite(right))
                    {
                        previousX = null;
                        previousDiff = null;
                        continue;
                    }

                    double diff = left - right;
                    double? root = null;
                    if (Math.Abs(diff) <= 1e-9 * (1 + Math.Abs(left) + Math.Abs(right)))
                    {
                        // 采样点恰好落在交点上（常见于整数解），直接用该点
                        root = x;
                    }
                    else if (previousDiff is { } lastDiff && previousX is { } lastX
                        && Math.Abs(lastDiff) < bracketLimit && Math.Abs(diff) < bracketLimit
                        && Math.Sign(lastDiff) != 0 && Math.Sign(lastDiff) != Math.Sign(diff))
                    {
                        root = Refine(curves[i], curves[j], lastX, x);
                    }

                    if (root is { } hit)
                    {
                        double y = curves[i].Evaluate(hit);
                        if (double.IsFinite(y)
                            && Math.Abs(curves[i].Evaluate(hit) - curves[j].Evaluate(hit)) < Math.Abs(spanY) * 1e-4
                            && y >= _yMin - spanY * 0.02 && y <= _yMax + spanY * 0.02
                            && !_intersections.Any(p => Math.Abs(p.X - hit) < minGap && Math.Abs(p.Y - y) < minGap))
                        {
                            _intersections.Add(new Point(hit, y));
                        }
                    }

                    previousX = x;
                    previousDiff = diff;
                }
            }
        }

        return _intersections;
    }

    /// <summary>在已确认异号的两点之间二分，逼近交点横坐标。</summary>
    private static double Refine(FunctionCurve first, FunctionCurve second, double low, double high)
    {
        double lowDiff = first.Evaluate(low) - second.Evaluate(low);
        for (int i = 0; i < 45; i++)
        {
            double mid = (low + high) / 2;
            double midDiff = first.Evaluate(mid) - second.Evaluate(mid);
            if (!double.IsFinite(midDiff)) break;
            if (Math.Sign(lowDiff) == Math.Sign(midDiff))
            {
                low = mid;
                lowDiff = midDiff;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    public override bool HitTest(Point point, double tolerance)
    {
        double reach = tolerance + _width * 0.5 + 2;
        foreach (var curve in GetCurves())
        {
            foreach (var segment in curve.GetSegments(_xMin, _xMax, _yMin, _yMax, _left, _top, _plotWidth, _plotHeight))
            {
                for (int i = 0; i < segment.Length - 1; i++)
                {
                    if (InkStroke.DistanceToSegment(point, segment[i], segment[i + 1]) <= reach) return true;
                }
            }
        }

        // 只有坐标轴也允许选中，方便在空白图上点中它
        var area = PlotArea;
        return area.Width > 1 && area.Height > 1 && area.Contains(point)
               && (_yMin < 0 && _yMax > 0 && Math.Abs(point.Y - MapY(0)) <= reach
                   || _xMin < 0 && _xMax > 0 && Math.Abs(point.X - MapX(0)) <= reach);
    }

    public override void Translate(Vector delta)
    {
        _left += delta.X;
        _top += delta.Y;
        Invalidate();
    }

    /// <summary>
    /// 依据所有曲线的采样结果估算一个稳定的纵轴范围：取 5%–95% 分位并留白，
    /// 这样 1/x 这类有渐近线的函数不会把纵轴撑到离谱。
    /// </summary>
    public static (double Min, double Max) AutoYRange(
        IEnumerable<(string Expression, double ParameterA)> curves, double xMin, double xMax)
    {
        var fallback = (-4.0, 4.0);
        var values = new List<double>(1024);
        const int samples = 256;
        double span = Math.Abs(xMax - xMin) < 1e-9 ? 1e-9 : xMax - xMin;

        foreach (var (text, parameterA) in curves)
        {
            if (!MathExpression.TryParse(text, out var expression, out _) || expression is null) continue;
            for (int i = 0; i <= samples; i++)
            {
                double x = xMin + span * i / samples;
                double y = expression.Evaluate(x, parameterA);
                if (double.IsFinite(y) && Math.Abs(y) < 1e6) values.Add(y);
            }
        }

        if (values.Count < 8) return fallback;

        values.Sort();
        double low = values[(int)(values.Count * 0.05)];
        double high = values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))];
        if (Math.Abs(high - low) < 1e-6)
        {
            low -= 1;
            high += 1;
        }

        double padding = (high - low) * 0.12;
        low -= padding;
        high += padding;
        return (low, high);
    }
}

/// <summary>坐标轴里的一条函数曲线：表达式、参数 a 与颜色各自独立。</summary>
public sealed class FunctionCurve
{
    [JsonIgnore] private MathExpression? _parsed;
    [JsonIgnore] private bool _parseFailed;
    [JsonIgnore] private List<Point[]>? _segments;
    [JsonIgnore] private string _cacheKey = string.Empty;
    [JsonIgnore] private IPen? _pen;
    [JsonIgnore] private double _penWidth = -1;
    [JsonIgnore] private uint _penColor;

    public string Expression { get; set; } = "sin(x)";

    /// <summary>原始 LaTeX 输入，仅用于再次编辑时回填。</summary>
    public string Latex { get; set; } = string.Empty;

    public double ParameterA { get; set; } = 1;

    public uint Color { get; set; } = 0xFFFFFFFF;

    [JsonIgnore] public bool ParseFailed => _parseFailed;

    public void Invalidate()
    {
        _parsed = null;
        _parseFailed = false;
        _segments = null;
        _cacheKey = string.Empty;
        _pen = null;
    }

    public IPen GetPen(double width)
    {
        if (_pen is not null && _penWidth == width && _penColor == Color) return _pen;
        var brush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
            (byte)(Color >> 24), (byte)(Color >> 16), (byte)(Color >> 8), (byte)Color)).ToImmutable();
        _pen = new Pen(brush, width)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }.ToImmutable();
        _penWidth = width;
        _penColor = Color;
        return _pen;
    }

    private MathExpression? GetExpression()
    {
        if (_parsed is null && !_parseFailed)
        {
            if (MathExpression.TryParse(Expression, out var parsed, out _) && parsed is not null)
            {
                _parsed = parsed;
                _parseFailed = false;
            }
            else
            {
                _parseFailed = true;
            }
        }

        return _parsed;
    }

    /// <summary>在当前参数 a 下求 f(x)，表达式无效时返回 NaN。</summary>
    public double Evaluate(double x) => GetExpression()?.Evaluate(x, ParameterA) ?? double.NaN;

    /// <summary>按当前坐标系把曲线采样成若干条折线（断崖/渐近线处会断开）。</summary>
    public List<Point[]> GetSegments(double xMin, double xMax, double yMin, double yMax,
        double left, double top, double plotWidth, double plotHeight)
    {
        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Expression}|{ParameterA}|{xMin}|{xMax}|{yMin}|{yMax}|{left}|{top}|{plotWidth}|{plotHeight}");
        if (_segments is not null && key == _cacheKey) return _segments;

        _segments = new List<Point[]>();
        _cacheKey = key;

        var expression = GetExpression();
        if (expression is null) return _segments;

        double spanX = Math.Abs(xMax - xMin) < 1e-9 ? 1e-9 : xMax - xMin;
        double spanY = Math.Abs(yMax - yMin) < 1e-9 ? 1e-9 : yMax - yMin;
        int samples = (int)Math.Clamp(plotWidth * 1.4, 240, 1800);
        double maxJump = plotHeight * 1.6;

        var points = new List<Point>(128);
        double? previousY = null;
        for (int i = 0; i <= samples; i++)
        {
            double x = xMin + spanX * i / samples;
            double y = expression.Evaluate(x, ParameterA);
            var mapped = new Point(
                left + (x - xMin) / spanX * plotWidth,
                top + plotHeight - (y - yMin) / spanY * plotHeight);

            bool finite = double.IsFinite(y) && double.IsFinite(mapped.Y);
            bool jumped = previousY is not null && Math.Abs(mapped.Y - previousY.Value) > maxJump;

            if (!finite || jumped)
            {
                Flush(points);
                previousY = null;
                if (!finite) continue;
            }

            points.Add(mapped);
            previousY = mapped.Y;
        }

        Flush(points);
        return _segments;

        void Flush(List<Point> buffer)
        {
            if (buffer.Count > 1) _segments!.Add(buffer.ToArray());
            buffer.Clear();
        }
    }
}
