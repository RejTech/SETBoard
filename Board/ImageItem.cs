using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SETBoard.Board;

/// <summary>插入的图片，原始字节内嵌在存档中，随白板一起保存。</summary>
public sealed class ImageItem : BoardItem
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private byte[] _data = Array.Empty<byte>();
    private Bitmap? _bitmap;

    public double X
    {
        get => _x;
        set { _x = value; Invalidate(); }
    }

    public double Y
    {
        get => _y;
        set { _y = value; Invalidate(); }
    }

    public double Width
    {
        get => _width;
        set { _width = value; Invalidate(); }
    }

    public double Height
    {
        get => _height;
        set { _height = value; Invalidate(); }
    }

    /// <summary>图片文件的原始字节（JSON 中以 base64 保存）。</summary>
    public byte[] Data
    {
        get => _data;
        set { _data = value ?? Array.Empty<byte>(); Invalidate(); }
    }

    [JsonIgnore]
    public override Rect Bounds => new(_x, _y, _width, _height);

    public override void Invalidate() => _bitmap = null;

    private Bitmap? GetBitmap()
    {
        if (_bitmap is not null || _data.Length == 0) return _bitmap;

        try
        {
            using var stream = new MemoryStream(_data);
            _bitmap = new Bitmap(stream);
        }
        catch
        {
            _bitmap = null;
        }

        return _bitmap;
    }

    public override void Draw(DrawingContext context)
    {
        var bitmap = GetBitmap();
        if (bitmap is null) return;

        var source = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        context.DrawImage(bitmap, source, Bounds);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var hitBox = new Rect(_x - tolerance, _y - tolerance,
            _width + tolerance * 2, _height + tolerance * 2);
        return hitBox.Contains(point);
    }

    public override void Translate(Vector delta)
    {
        _x += delta.X;
        _y += delta.Y;
    }
}
