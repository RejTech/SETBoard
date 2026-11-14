using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SETBoard.Board;

public sealed partial class BoardCanvas
{
    /// <summary>像素 alpha 大于该值就当成线条（填充的边界），小于则视为可填充区域。</summary>
    private const byte FillWallAlpha = 16;

    /// <summary>填色：优先给三维几何体的面填色，否则给手绘笔画围出的闭合区域填色。</summary>
    private void FillAt(Point world, Point screen)
    {
        if (PickItem(world, 8 / _zoom) is SolidItem solid)
        {
            if (!solid.TryPickFace(world, out int face)) return;

            uint? previous = solid.GetFaceColor(face);
            uint? next = previous == InkColor ? null : InkColor;
            solid.SetFaceColor(face, next);
            History.Push(new FillSolidFaceCommand(solid, face, previous, next));
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        FillInkRegion(screen);
    }

    /// <summary>
    /// 把当前画面按屏幕像素离屏渲染一遍，从点击位置做扫描线泛洪填充，
    /// 只保留四周被线条围住的区域，生成一张填色图片垫在笔画下面。
    /// </summary>
    private void FillInkRegion(Point screen)
    {
        int width = (int)Math.Ceiling(Bounds.Width);
        int height = (int)Math.Ceiling(Bounds.Height);
        int startX = (int)Math.Round(screen.X);
        int startY = (int)Math.Round(screen.Y);
        if (width < 8 || height < 8 || startX < 0 || startY < 0 || startX >= width || startY >= height) return;

        byte[] pixels;
        using (var raster = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96)))
        {
            using (var context = raster.CreateDrawingContext())
            {
                using (context.PushTransform(
                           Matrix.CreateScale(_zoom, _zoom) *
                           Matrix.CreateTranslation(_offset.X, _offset.Y)))
                {
                    var visible = VisibleWorldRect();
                    foreach (var item in _document.Items)
                    {
                        if (item.Bounds.Intersects(visible)) item.Draw(context);
                    }
                }
            }

            pixels = CopyPixels(raster, width, height);
        }

        // 点在笔画上时没有可填充的空白
        if (pixels[(startY * width + startX) * 4 + 3] > FillWallAlpha) return;

        if (!FloodFill(pixels, width, height, startX, startY, out byte[] mask, out PixelRect bounds)) return;

        var image = CreateFillImage(pixels, mask, width, bounds);
        if (image is null) return;

        // 垫在所有对象下面，保持笔画在最上层
        _document.Items.Insert(0, image);
        History.Push(new ChangeCommand(Array.Empty<BoardItem>(), new[] { image }));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>把离屏渲染结果读成 BGRA 像素数组（alpha 在第 4 个字节）。</summary>
    private static byte[] CopyPixels(RenderTargetBitmap bitmap, int width, int height)
    {
        int stride = width * 4;
        int size = stride * height;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, size, stride);
            var pixels = new byte[size];
            Marshal.Copy(buffer, pixels, 0, size);
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 扫描线泛洪：从起点向四周扩散，遇到 alpha 足够高的像素停下。
    /// 区域一旦碰到画面边缘说明没有闭合，返回 false。
    /// </summary>
    private static bool FloodFill(byte[] pixels, int width, int height, int startX, int startY,
        out byte[] mask, out PixelRect bounds)
    {
        var filled = new byte[width * height];
        mask = filled;
        bounds = default;

        int minX = startX, maxX = startX, minY = startY, maxY = startY;
        bool touchesEdge = false;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((startX, startY));

        bool Free(int x, int y)
        {
            int i = y * width + x;
            return filled[i] == 0 && pixels[i * 4 + 3] <= FillWallAlpha;
        }

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (!Free(x, y)) continue;

            int left = x;
            while (left > 0 && Free(left - 1, y)) left--;
            int right = x;
            while (right < width - 1 && Free(right + 1, y)) right++;

            int row = y * width;
            for (int i = left; i <= right; i++) mask[row + i] = 1;

            if (left == 0 || right == width - 1 || y == 0 || y == height - 1) touchesEdge = true;
            if (left < minX) minX = left;
            if (right > maxX) maxX = right;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;

            // 上下两行里找出连续的可填充段，各压入一个种子
            for (int ny = y - 1; ny <= y + 1; ny += 2)
            {
                if (ny < 0 || ny >= height) continue;
                bool inSpan = false;
                for (int i = left; i <= right; i++)
                {
                    bool open = Free(i, ny);
                    if (open && !inSpan)
                    {
                        stack.Push((i, ny));
                        inSpan = true;
                    }
                    else if (!open)
                    {
                        inSpan = false;
                    }
                }
            }
        }

        if (touchesEdge) return false;

        // 向外扩 1 像素，盖住线条抗锯齿留下的半透明边缘，避免填色和笔画之间出现细缝
        // （写入副本，只读原始掩码，避免一圈圈向外蔓延）
        var grown = (byte[])mask.Clone();
        int growX0 = Math.Max(0, minX - 1), growX1 = Math.Min(width - 1, maxX + 1);
        int growY0 = Math.Max(0, minY - 1), growY1 = Math.Min(height - 1, maxY + 1);
        for (int y = growY0; y <= growY1; y++)
        {
            for (int x = growX0; x <= growX1; x++)
            {
                int i = y * width + x;
                if (mask[i] != 0 || pixels[i * 4 + 3] > 200) continue;
                if (x > 0 && mask[i - 1] != 0
                    || x < width - 1 && mask[i + 1] != 0
                    || y > 0 && mask[i - width] != 0
                    || y < height - 1 && mask[i + width] != 0)
                {
                    grown[i] = 1;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        mask = grown;

        bounds = new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }

    /// <summary>把填充区域按屏幕像素裁切出来，转成世界坐标下的图片对象。</summary>
    private ImageItem? CreateFillImage(byte[] pixels, byte[] mask, int width, PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        byte a = (byte)(InkColor >> 24);
        byte r = (byte)(InkColor >> 16);
        byte g = (byte)(InkColor >> 8);
        byte b = (byte)InkColor;
        int stride = bounds.Width * 4;
        var row = new byte[stride];

        using var bitmap = new WriteableBitmap(
            new PixelSize(bounds.Width, bounds.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            for (int y = 0; y < bounds.Height; y++)
            {
                Array.Clear(row);
                int source = (bounds.Y + y) * width + bounds.X;
                for (int x = 0; x < bounds.Width; x++)
                {
                    if (mask[source + x] == 0) continue;
                    int offset = x * 4;
                    row[offset] = (byte)(b * a / 255);
                    row[offset + 1] = (byte)(g * a / 255);
                    row[offset + 2] = (byte)(r * a / 255);
                    row[offset + 3] = a;
                }

                Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, stride);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);

        return new ImageItem
        {
            X = (bounds.X - _offset.X) / _zoom,
            Y = (bounds.Y - _offset.Y) / _zoom,
            Width = bounds.Width / _zoom,
            Height = bounds.Height / _zoom,
            Data = stream.ToArray(),
        };
    }
}
