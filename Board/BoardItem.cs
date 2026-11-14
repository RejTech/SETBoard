using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace SETBoard.Board;

/// <summary>画布上的一个对象（墨迹或图形）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(InkStroke), "ink")]
[JsonDerivedType(typeof(ShapeItem), "shape")]
[JsonDerivedType(typeof(FunctionItem), "function")]
[JsonDerivedType(typeof(SolidItem), "solid")]
[JsonDerivedType(typeof(ImageItem), "image")]
public abstract class BoardItem
{
    [JsonIgnore] public abstract Rect Bounds { get; }

    public abstract void Draw(DrawingContext context);

    /// <summary>命中测试，容差为世界坐标单位。</summary>
    public abstract bool HitTest(Point point, double tolerance);

    public abstract void Translate(Vector delta);

    public abstract void Invalidate();
}
