using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;

namespace SETBoard.Board;

public sealed class BoardPage
{
    public List<BoardItem> Items { get; set; } = new();
}

public sealed class BoardDocument
{
    public List<BoardPage> Pages { get; set; } = new() { new BoardPage() };
    public bool ShowGrid { get; set; } = true;
    public double GridSize { get; set; } = 36;
    public uint Background { get; set; } = 0xFF0C1B24;

    /// <summary>用另一份文档的内容替换当前文档（用于打开存档）。</summary>
    public void ReplaceWith(BoardDocument other)
    {
        Pages.Clear();
        if (other.Pages.Count == 0) other.Pages.Add(new BoardPage());
        Pages.AddRange(other.Pages);
        ShowGrid = other.ShowGrid;
        GridSize = other.GridSize;
        Background = other.Background;
        _currentPageIndex = 0;
    }

    /// <summary>兼容早期单页存档。</summary>
    [JsonPropertyName("Items")]
    public List<BoardItem>? LegacyItems { get; set; }

    [JsonIgnore]
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set => _currentPageIndex = value;
    }

    private int _currentPageIndex;

    [JsonIgnore]
    public BoardPage CurrentPage
    {
        get
        {
            if (Pages.Count == 0) Pages.Add(new BoardPage());
            _currentPageIndex = Math.Clamp(_currentPageIndex, 0, Pages.Count - 1);
            return Pages[_currentPageIndex];
        }
    }

    [JsonIgnore] public List<BoardItem> Items => CurrentPage.Items;

    public Rect ContentBounds(double padding)
    {
        if (Items.Count == 0) return default;

        bool first = true;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var item in Items)
        {
            var b = item.Bounds;
            if (b.Width <= 0 && b.Height <= 0) continue;
            if (first)
            {
                minX = b.X;
                minY = b.Y;
                maxX = b.Right;
                maxY = b.Bottom;
                first = false;
            }
            else
            {
                minX = Math.Min(minX, b.X);
                minY = Math.Min(minY, b.Y);
                maxX = Math.Max(maxX, b.Right);
                maxY = Math.Max(maxY, b.Bottom);
            }
        }

        if (first) return default;
        return new Rect(minX - padding, minY - padding,
            maxX - minX + padding * 2, maxY - minY + padding * 2);
    }
}

public sealed class BoardFile
{
    public int Version { get; set; } = 2;
    public BoardDocument Document { get; set; } = new();
}

public static class BoardSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(BoardDocument document, Stream stream)
    {
        var file = new BoardFile { Document = document };
        JsonSerializer.Serialize(stream, file, Options);
    }

    public static BoardDocument Load(Stream stream)
    {
        var file = JsonSerializer.Deserialize<BoardFile>(stream, Options);
        var document = file?.Document ?? new BoardDocument();

        document.Pages ??= new List<BoardPage>();
        document.Pages.RemoveAll(page => page is null);
        if (document.Pages.Count == 0) document.Pages.Add(new BoardPage());

        foreach (var page in document.Pages)
        {
            page.Items ??= new List<BoardItem>();
            foreach (var item in page.Items) item.Invalidate();
        }

        if (document.LegacyItems is { Count: > 0 })
        {
            document.Pages[0].Items = document.LegacyItems;
        }

        document.LegacyItems = null;
        document.CurrentPageIndex = 0;
        return document;
    }
}
