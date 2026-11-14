using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace SETBoard.Board;

public enum SolidKind
{
    Cube,
    Cuboid,
    Pyramid,
    Cylinder,
    Cone,
    Sphere,
}

/// <summary>
/// 三维几何体：正交投影 + 画家算法填充，可见棱用实线、被遮挡的棱用虚线，
/// 与教材上的立体图一致。选中后拖动即可改变偏航角/俯仰角。
/// </summary>
public sealed class SolidItem : BoardItem
{
    private SolidKind _kind = SolidKind.Cube;
    private double _centerX;
    private double _centerY;
    private double _size = 160;
    private double _height = 160;
    private double _yaw = 28;
    private double _pitch = 16;
    private uint _color = 0xFFFFFFFF;
    private double _width = 2.2;

    private IPen? _edgePen;
    private IPen? _hiddenPen;
    private IBrush? _faceBrush;
    private readonly Dictionary<int, uint> _faceColors = new();

    /// <summary>几何体中心（世界坐标）。</summary>
    public double CenterX
    {
        get => _centerX;
        set { _centerX = value; Invalidate(); }
    }

    public double CenterY
    {
        get => _centerY;
        set { _centerY = value; Invalidate(); }
    }

    public SolidKind Kind
    {
        get => _kind;
        set { _kind = value; Invalidate(); }
    }

    /// <summary>底面尺寸（世界单位）。</summary>
    public double Size
    {
        get => _size;
        set { _size = Math.Clamp(value, 20, 5000); Invalidate(); }
    }

    /// <summary>高度（世界单位）。</summary>
    public double Height
    {
        get => _height;
        set { _height = Math.Clamp(value, 20, 5000); Invalidate(); }
    }

    /// <summary>绕竖直轴的旋转角（度）。</summary>
    public double Yaw
    {
        get => _yaw;
        set { _yaw = value; Invalidate(); }
    }

    /// <summary>绕水平轴的旋转角（度），限制在 ±85 以免图形倒置。</summary>
    public double Pitch
    {
        get => _pitch;
        set { _pitch = Math.Clamp(value, -85, 85); Invalidate(); }
    }

    public uint Color
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    public double Width
    {
        get => _width;
        set { _width = Math.Clamp(value, 0.5, 20); Invalidate(); }
    }

    /// <summary>各面的填充色，键为面编号（编号见 <see cref="TryPickFace"/> 的说明）。</summary>
    public Dictionary<int, uint> FaceColors
    {
        get => _faceColors;
        set
        {
            _faceColors.Clear();
            if (value is null) return;
            foreach (var pair in value) _faceColors[pair.Key] = pair.Value;
        }
    }

    public uint? GetFaceColor(int face) => _faceColors.TryGetValue(face, out uint color) ? color : null;

    public void SetFaceColor(int face, uint? color)
    {
        if (color is null)
        {
            _faceColors.Remove(face);
        }
        else
        {
            _faceColors[face] = color.Value;
        }
    }

    [JsonIgnore]
    public override Rect Bounds
    {
        get
        {
            double reach = Math.Max(_size, _height) * 0.72 + _width;
            return new Rect(_centerX - reach, _centerY - reach, reach * 2, reach * 2);
        }
    }

    public override void Invalidate()
    {
        _edgePen = null;
        _hiddenPen = null;
        _faceBrush = null;
    }

    public override void Translate(Vector delta)
    {
        _centerX += delta.X;
        _centerY += delta.Y;
    }

    public override bool HitTest(Point point, double tolerance) =>
        Bounds.Inflate(tolerance).Contains(point);

    /// <summary>
    /// 判断点中了哪个面，用于单面填色。
    /// 棱柱/棱锥用网格面的编号；圆柱 0=顶面 1=侧面；圆锥 0=侧面；球 0=球面。
    /// </summary>
    public bool TryPickFace(Point point, out int face)
    {
        face = -1;
        if (_kind == SolidKind.Sphere)
        {
            if (!EllipseContains(new Point(_centerX, _centerY), _size / 2, _size / 2, point)) return false;
            face = 0;
            return true;
        }

        return _kind is SolidKind.Cylinder or SolidKind.Cone
            ? TryPickCurvedFace(point, out face)
            : TryPickPolyhedronFace(point, out face);
    }

    private bool TryPickPolyhedronFace(Point point, out int face)
    {
        face = -1;
        var (_, projected, faces, visible, depth) = ProjectMesh();
        double best = double.MinValue;
        for (int i = 0; i < faces.Length; i++)
        {
            if (!visible[i]) continue;
            var polygon = new Point[faces[i].Length];
            for (int k = 0; k < polygon.Length; k++) polygon[k] = projected[faces[i][k]];
            if (!PolygonContains(polygon, point)) continue;
            if (depth[i] <= best) continue;
            best = depth[i];
            face = i;
        }

        return face >= 0;
    }

    private bool TryPickCurvedFace(Point point, out int face)
    {
        face = -1;
        double radius = _size / 2;
        double half = _height / 2;
        double pitch = _pitch * Math.PI / 180.0;
        var topCenter = Project(Rotate(new Vec3(0, half, 0)));
        var bottomCenter = Project(Rotate(new Vec3(0, -half, 0)));
        double ry = Math.Max(radius * Math.Abs(Math.Sin(pitch)), 1.0);

        if (_kind == SolidKind.Cylinder)
        {
            if (EllipseContains(topCenter, radius, ry, point))
            {
                face = 0;
                return true;
            }

            if (Math.Abs(point.X - topCenter.X) <= radius
                && point.Y >= topCenter.Y - ry && point.Y <= bottomCenter.Y + ry)
            {
                face = 1;
                return true;
            }

            return false;
        }

        // 圆锥：顶点 + 底圆围成的投影区域就是侧面的可见轮廓
        var apex = Project(Rotate(new Vec3(0, half, 0)));
        var (ring, ringDepth) = BuildBottomRing();
        var polygon = new List<Point> { apex };
        AppendFrontArc(polygon, ring, ringDepth, reverse: false);
        if (!PolygonContains(polygon.ToArray(), point)) return false;
        face = 0;
        return true;
    }

    private static bool EllipseContains(Point center, double rx, double ry, Point point)
    {
        double dx = (point.X - center.X) / Math.Max(rx, 0.001);
        double dy = (point.Y - center.Y) / Math.Max(ry, 0.001);
        return dx * dx + dy * dy <= 1.0;
    }

    private static bool PolygonContains(Point[] polygon, Point point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].Y > point.Y) == (polygon[j].Y > point.Y)) continue;
            double x = (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y)
                       / (polygon[j].Y - polygon[i].Y) + polygon[i].X;
            if (point.X < x) inside = !inside;
        }

        return inside;
    }

    // ---------------- 绘制 ----------------

    public override void Draw(DrawingContext context)
    {
        if (_kind == SolidKind.Sphere)
        {
            DrawSphere(context);
            return;
        }

        if (_kind is SolidKind.Cylinder or SolidKind.Cone)
        {
            DrawCurved(context);
            return;
        }

        DrawPolyhedron(context);
    }

    private IPen GetEdgePen()
    {
        if (_edgePen is null)
        {
            _edgePen = new Pen(ColorBrush(1.0), _width)
            {
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }.ToImmutable();
        }

        return _edgePen;
    }

    private IPen GetHiddenPen()
    {
        if (_hiddenPen is null)
        {
            _hiddenPen = new Pen(ColorBrush(0.42), _width * 0.85,
                new DashStyle(new double[] { 5, 4 }, 0)).ToImmutable();
        }

        return _hiddenPen;
    }

    private IBrush GetFaceBrush()
    {
        _faceBrush ??= ColorBrush(0.1).ToImmutable();
        return _faceBrush;
    }

    /// <summary>面有填色时用实色，否则用很淡的同色底，避免挡住虚线。</summary>
    private IBrush GetFaceBrush(int face)
    {
        if (!_faceColors.TryGetValue(face, out uint color)) return GetFaceBrush();
        return new SolidColorBrush(Avalonia.Media.Color.FromArgb(
            (byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }

    private IBrush ColorBrush(double alpha)
    {
        byte a = (byte)Math.Clamp((int)((byte)(_color >> 24) * alpha), 0, 255);
        return new SolidColorBrush(Avalonia.Media.Color.FromArgb(
            a, (byte)(_color >> 16), (byte)(_color >> 8), (byte)_color));
    }

    private readonly record struct Vec3(double X, double Y, double Z);

    /// <summary>先绕 Y 轴偏航，再绕 X 轴俯仰，得到观察坐标（Z 越大越靠近观察者）。</summary>
    private Vec3 Rotate(Vec3 v)
    {
        double yaw = _yaw * Math.PI / 180.0;
        double pitch = _pitch * Math.PI / 180.0;
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);

        double x = v.X * cy + v.Z * sy;
        double z = -v.X * sy + v.Z * cy;
        double y = v.Y * cp - z * sp;
        double z2 = v.Y * sp + z * cp;
        return new Vec3(x, y, z2);
    }

    private Point Project(Vec3 v) => new(_centerX + v.X, _centerY - v.Y);

    private void DrawPolyhedron(DrawingContext context)
    {
        var (rotated, projected, faces, faceVisible, faceDepth) = ProjectMesh();
        var edges = CollectEdges(faces, faceVisible);

        var edgePen = GetEdgePen();
        var hiddenPen = GetHiddenPen();

        // 先画被挡住的棱：之后画的面如果是实色，会把它们自然盖住
        foreach (var (edge, visible) in edges)
        {
            if (!visible) context.DrawLine(hiddenPen, projected[edge.A], projected[edge.B]);
        }

        // 画家算法：远的先画，近的后画
        var order = Enumerable.Range(0, faces.Length).OrderBy(i => faceDepth[i]).ToArray();
        foreach (int i in order)
        {
            if (!faceVisible[i]) continue;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(projected[faces[i][0]], true);
                for (int k = 1; k < faces[i].Length; k++) ctx.LineTo(projected[faces[i][k]]);
                ctx.EndFigure(true);
            }

            context.DrawGeometry(GetFaceBrush(i), null, geometry);
        }

        foreach (var (edge, visible) in edges)
        {
            if (visible) context.DrawLine(edgePen, projected[edge.A], projected[edge.B]);
        }
    }

    private (Vec3[] Rotated, Point[] Projected, int[][] Faces, bool[] Visible, double[] Depth) ProjectMesh()
    {
        var (vertices, faces) = BuildMesh();
        var rotated = new Vec3[vertices.Length];
        var projected = new Point[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            rotated[i] = Rotate(vertices[i]);
            projected[i] = Project(rotated[i]);
        }

        var visible = new bool[faces.Length];
        var depth = new double[faces.Length];
        for (int i = 0; i < faces.Length; i++)
        {
            Vec3 a = rotated[faces[i][0]], b = rotated[faces[i][1]], c = rotated[faces[i][2]];
            var normal = Cross(Sub(b, a), Sub(c, a));
            visible[i] = normal.Z > 0;
            double sum = 0;
            foreach (int index in faces[i]) sum += rotated[index].Z;
            depth[i] = sum / faces[i].Length;
        }

        return (rotated, projected, faces, visible, depth);
    }
    /// <summary>统计每条棱是否属于某个可见面（属于则画实线，否则画虚线）。</summary>
    private static Dictionary<(int A, int B), bool> CollectEdges(int[][] faces, bool[] faceVisible)
    {
        var edges = new Dictionary<(int A, int B), bool>();
        foreach (var (a, b) in EnumerateEdges(faces))
        {
            bool visible = false;
            for (int f = 0; f < faces.Length; f++)
            {
                if (!faceVisible[f]) continue;
                if (Array.IndexOf(faces[f], a) >= 0 && Array.IndexOf(faces[f], b) >= 0)
                {
                    visible = true;
                    break;
                }
            }

            var key = a < b ? (a, b) : (b, a);
            edges[key] = edges.TryGetValue(key, out bool existing) ? existing || visible : visible;
        }

        return edges;
    }

    private static IEnumerable<(int A, int B)> EnumerateEdges(int[][] faces)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var face in faces)
        {
            for (int i = 0; i < face.Length; i++)
            {
                int a = face[i];
                int b = face[(i + 1) % face.Length];
                var key = a < b ? (a, b) : (b, a);
                if (seen.Add(key)) yield return key;
            }
        }
    }

    private static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>返回局部坐标下的顶点与面（面按逆时针记录，法向朝外）。</summary>
    private (Vec3[] Vertices, int[][] Faces) BuildMesh()
    {
        if (_kind == SolidKind.Pyramid)
        {
            double hx = _size / 2, hy = _height / 2, hz = _size / 2;
            var vertices = new[]
            {
                new Vec3(-hx, -hy, -hz),
                new Vec3(hx, -hy, -hz),
                new Vec3(hx, -hy, hz),
                new Vec3(-hx, -hy, hz),
                new Vec3(0, hy, 0),
            };
            var faces = new[]
            {
                new[] { 0, 1, 2, 3 }, // 底面（法向朝下）
                new[] { 0, 4, 1 },
                new[] { 1, 4, 2 },
                new[] { 2, 4, 3 },
                new[] { 3, 4, 0 },
            };
            return (vertices, faces);
        }

        // 正方体 / 长方体：深度取 Size，高度取 Height
        double depth = _kind == SolidKind.Cube ? _size : _size * 0.72;
        double x = _size / 2, y = _height / 2, z = depth / 2;
        var v = new[]
        {
            new Vec3(-x, -y, -z), new Vec3(x, -y, -z), new Vec3(x, -y, z), new Vec3(-x, -y, z),
            new Vec3(-x, y, -z), new Vec3(x, y, -z), new Vec3(x, y, z), new Vec3(-x, y, z),
        };
        var f = new[]
        {
            new[] { 0, 1, 2, 3 }, // 下
            new[] { 4, 7, 6, 5 }, // 上
            new[] { 0, 4, 5, 1 }, // 前
            new[] { 1, 5, 6, 2 }, // 右
            new[] { 2, 6, 7, 3 }, // 后
            new[] { 3, 7, 4, 0 }, // 左
        };
        return (v, f);
    }

    /// <summary>圆柱 / 圆锥：椭圆 + 侧轮廓线，被挡住的椭圆或轮廓用虚线。</summary>
    private void DrawCurved(DrawingContext context)
    {
        double sinePitch = Math.Sin(_pitch * Math.PI / 180.0);
        var top = BuildRing(_height / 2, out double[] topDepth);
        var bottom = BuildRing(-_height / 2, out double[] bottomDepth);
        int segments = top.Length;

        var edgePen = GetEdgePen();
        var hiddenPen = GetHiddenPen();

        if (_kind == SolidKind.Cylinder)
        {
            // 顶面朝上时完全可见；底面被柱身挡住，只露出前半
            bool topVisible = sinePitch > 0;
            if (topVisible)
            {
                DrawRingHalf(context, hiddenPen, bottom, bottomDepth, front: false);
            }
            else
            {
                DrawRing(context, hiddenPen, top);
            }

            if (topVisible) FillRing(context, top, face: 0);
            FillCylinderSide(context, top, topDepth, bottom, bottomDepth, face: 1);

            DrawRing(context, topVisible ? edgePen : hiddenPen, top);
            if (topVisible) DrawRingHalf(context, edgePen, bottom, bottomDepth, front: true);
            else DrawRing(context, edgePen, bottom);

            int minIndex = 0, maxIndex = 0;
            for (int i = 1; i < segments; i++)
            {
                if (top[i].X < top[minIndex].X) minIndex = i;
                if (top[i].X > top[maxIndex].X) maxIndex = i;
            }

            context.DrawLine(edgePen, top[minIndex], bottom[minIndex]);
            context.DrawLine(edgePen, top[maxIndex], bottom[maxIndex]);
            return;
        }

        // 圆锥：底面椭圆被侧面挡住，后半画虚线，母线连到投影最左/最右的底圆点
        DrawRingHalf(context, hiddenPen, bottom, bottomDepth, front: false);

        var apex = Project(Rotate(new Vec3(0, _height / 2, 0)));
        FillConeSide(context, apex, bottom, bottomDepth, face: 0);

        DrawRingHalf(context, edgePen, bottom, bottomDepth, front: true);

        int left = 0, right = 0;
        for (int i = 1; i < segments; i++)
        {
            if (bottom[i].X < bottom[left].X) left = i;
            if (bottom[i].X > bottom[right].X) right = i;
        }

        context.DrawLine(edgePen, apex, bottom[left]);
        context.DrawLine(edgePen, apex, bottom[right]);
    }

    private Point[] BuildRing(double y, out double[] depth)
    {
        const int segments = 48;
        double radius = _size / 2;
        var points = new Point[segments];
        depth = new double[segments];
        for (int i = 0; i < segments; i++)
        {
            double angle = Math.PI * 2 * i / segments;
            var rotated = Rotate(new Vec3(radius * Math.Cos(angle), y, radius * Math.Sin(angle)));
            points[i] = Project(rotated);
            depth[i] = rotated.Z;
        }

        return points;
    }

    private (Point[] Points, double[] Depth) BuildBottomRing()
    {
        var points = BuildRing(-_height / 2, out var depth);
        return (points, depth);
    }

    /// <summary>圆柱侧面：上下两个椭圆的前半围成的区域。</summary>
    private void FillCylinderSide(DrawingContext context, Point[] top, double[] topDepth,
        Point[] bottom, double[] bottomDepth, int face)
    {
        if (!_faceColors.ContainsKey(face)) return;
        var polygon = new List<Point>();
        AppendFrontArc(polygon, top, topDepth, reverse: false);
        AppendFrontArc(polygon, bottom, bottomDepth, reverse: true);
        DrawPolygon(context, polygon, GetFaceBrush(face));
    }

    /// <summary>圆锥侧面：顶点与底圆前半围成的投影区域。</summary>
    private void FillConeSide(DrawingContext context, Point apex, Point[] bottom, double[] bottomDepth, int face)
    {
        if (!_faceColors.ContainsKey(face)) return;
        var polygon = new List<Point> { apex };
        AppendFrontArc(polygon, bottom, bottomDepth, reverse: false);
        DrawPolygon(context, polygon, GetFaceBrush(face));
    }

    private void FillRing(DrawingContext context, Point[] ring, int face)
    {
        if (!_faceColors.ContainsKey(face)) return;
        DrawPolygon(context, ring, GetFaceBrush(face));
    }

    private static void AppendFrontArc(List<Point> target, Point[] points, double[] depth, bool reverse)
    {
        var arc = new List<Point>();
        for (int i = 0; i < points.Length; i++)
        {
            if (depth[i] >= 0) arc.Add(points[i]);
        }

        if (reverse) arc.Reverse();
        target.AddRange(arc);
    }

    private static void DrawPolygon(DrawingContext context, IReadOnlyList<Point> points, IBrush brush)
    {
        if (points.Count < 3) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i]);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    /// <summary>只画椭圆的前半或后半（按深度判断），与教材画法一致。</summary>
    private static void DrawRingHalf(DrawingContext context, IPen pen, Point[] points, double[] depth, bool front)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < points.Length; i++)
            {
                int next = (i + 1) % points.Length;
                if ((depth[i] + depth[next] >= 0) != front) continue;
                ctx.BeginFigure(points[i], false);
                ctx.LineTo(points[next]);
                ctx.EndFigure(false);
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawRing(DrawingContext context, IPen pen, Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i]);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>球：外轮廓圆 + 两条大圆（赤道与经线），背面的部分画虚线。</summary>
    private void DrawSphere(DrawingContext context)
    {
        const int segments = 64;
        double radius = _size / 2;
        var center = new Point(_centerX, _centerY);

        if (_faceColors.ContainsKey(0))
        {
            context.DrawEllipse(GetFaceBrush(0), null, center, radius, radius);
        }

        context.DrawEllipse(null, GetEdgePen(), center, radius, radius);

        DrawGreatCircle(context, segments, radius, vertical: false);
        DrawGreatCircle(context, segments, radius, vertical: true);
    }

    private void DrawGreatCircle(DrawingContext context, int segments, double radius, bool vertical)
    {
        var solid = new StreamGeometry();
        var dashed = new StreamGeometry();
        Point? previous = null;
        bool? previousVisible = null;

        using (var solidCtx = solid.Open())
        using (var dashedCtx = dashed.Open())
        {
            StreamGeometryContext? active = null;

            for (int i = 0; i <= segments; i++)
            {
                double angle = Math.PI * 2 * i / segments;
                double a = radius * Math.Cos(angle);
                double b = radius * Math.Sin(angle);
                var local = vertical ? new Vec3(a, b, 0) : new Vec3(a, 0, b);
                var rotated = Rotate(local);
                var point = Project(rotated);
                bool visible = rotated.Z >= 0;

                if (previous is not null && previousVisible.HasValue && previousVisible.Value == visible)
                {
                    active?.LineTo(point);
                }
                else
                {
                    active?.EndFigure(false);
                    active = visible ? solidCtx : dashedCtx;
                    active.BeginFigure(point, false);
                }

                previous = point;
                previousVisible = visible;
            }

            active?.EndFigure(false);
        }

        context.DrawGeometry(null, GetHiddenPen(), dashed);
        context.DrawGeometry(null, GetEdgePen(), solid);
    }
}
