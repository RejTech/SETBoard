using Avalonia;

namespace SETBoard.Board;

public sealed partial class BoardCanvas
{
    /// <summary>直读输入的指针 Id 用高位标记，和 Avalonia 的指针 Id 不会冲突。</summary>
    private const int RawPointerIdBase = 0x40000000;

    private int _rawContactId = -1;
    private bool _rawPenDown;
    private long _rawPenTick;
    private IReadOnlyList<Rect> _inkExclusions = Array.Empty<Rect>();

    /// <summary>设置不接收笔迹的区域（画布坐标），用于避开悬浮工具栏。</summary>
    public void SetInkExclusions(IReadOnlyList<Rect>? rects) =>
        _inkExclusions = rects ?? Array.Empty<Rect>();

    /// <summary>该屏幕点是否落在笔迹禁区内。</summary>
    public bool IsInkExcluded(Point screen)
    {
        for (int i = 0; i < _inkExclusions.Count; i++)
        {
            if (_inkExclusions[i].Contains(screen)) return true;
        }

        return false;
    }

    /// <summary>笔尖按下的时间窗口内，触摸一律视为掌触。</summary>
    public bool IsRawPenActive =>
        _rawPenDown && Environment.TickCount64 - _rawPenTick < 1500;

    /// <summary>直读的落点：立即开笔，不等 Avalonia 的指针事件排队。</summary>
    public void RawPointerDown(int contactId, Point screen, double pressure, bool isPen)
    {
        _viewAnimationToken++;
        if (isPen)
        {
            _rawPenDown = true;
            _rawPenTick = Environment.TickCount64;
        }
        else if (IsRawPenActive)
        {
            return;
        }

        if (_interaction != Interaction.None) return;
        if (Tool is not (BoardTool.Pen or BoardTool.Highlighter)) return;
        if (IsInkExcluded(screen)) return;

        Focus();
        EnsureCamera();
        StartInk(ToWorld(screen), pressure, Tool == BoardTool.Highlighter);

        _rawContactId = contactId;
        _activePointerId = RawPointerIdBase + contactId;
    }

    /// <summary>直读的移动采样，直接续到正在写的笔迹上。</summary>
    public void RawPointerMove(int contactId, Point screen, double pressure)
    {
        if (_interaction != Interaction.Drawing || contactId != _rawContactId) return;
        if (IsInkExcluded(screen)) return;

        _liveStroke?.AddPoint(ToWorld(screen), pressure, 1.0 / _zoom);
        InvalidateVisual();
    }

    /// <summary>直读的抬笔。</summary>
    public void RawPointerUp(int contactId, bool isPen)
    {
        if (isPen) _rawPenDown = false;
        if (contactId != _rawContactId) return;

        _rawContactId = -1;
        if (_interaction == Interaction.Drawing) FinishInteraction();
    }
}
