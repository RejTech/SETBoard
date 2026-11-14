using Avalonia;

namespace SETBoard.Board;

public interface IBoardCommand
{
    void Undo(BoardDocument document);
    void Redo(BoardDocument document);
}

public sealed class UndoStack
{
    private readonly List<IBoardCommand> _undo = new();
    private readonly List<IBoardCommand> _redo = new();

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>登记一个已经应用到文档上的命令。</summary>
    public void Push(IBoardCommand command)
    {
        _undo.Add(command);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo(BoardDocument document)
    {
        if (_undo.Count == 0) return;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo(document);
        _redo.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo(BoardDocument document)
    {
        if (_redo.Count == 0) return;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Redo(document);
        _undo.Add(command);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>同时记录被移除对象与新增对象的通用变更（新增墨迹、局部擦除、删除、清空）。</summary>
public sealed class ChangeCommand : IBoardCommand
{
    private readonly List<BoardItem> _removed;
    private readonly List<BoardItem> _added;

    public ChangeCommand(IEnumerable<BoardItem> removed, IEnumerable<BoardItem> added)
    {
        _removed = removed.ToList();
        _added = added.ToList();
    }

    public void Undo(BoardDocument document)
    {
        foreach (var item in _added)
        {
            document.Items.Remove(item);
        }

        document.Items.AddRange(_removed);
    }

    public void Redo(BoardDocument document)
    {
        foreach (var item in _removed)
        {
            document.Items.Remove(item);
        }

        document.Items.AddRange(_added);
    }
}

public sealed class MoveItemsCommand : IBoardCommand
{
    private readonly List<BoardItem> _items;
    private readonly Vector _delta;

    public MoveItemsCommand(IEnumerable<BoardItem> items, Vector delta)
    {
        _items = items.ToList();
        _delta = delta;
    }

    public void Undo(BoardDocument document)
    {
        var inverse = new Vector(-_delta.X, -_delta.Y);
        foreach (var item in _items) item.Translate(inverse);
    }

    public void Redo(BoardDocument document)
    {
        foreach (var item in _items) item.Translate(_delta);
    }
}

/// <summary>立体图形的三维旋转（偏航、俯仰）。</summary>
public sealed class RotateSolidCommand : IBoardCommand
{
    private readonly SolidItem _item;
    private readonly double _oldYaw;
    private readonly double _oldPitch;
    private readonly double _newYaw;
    private readonly double _newPitch;

    public RotateSolidCommand(SolidItem item, double oldYaw, double oldPitch, double newYaw, double newPitch)
    {
        _item = item;
        _oldYaw = oldYaw;
        _oldPitch = oldPitch;
        _newYaw = newYaw;
        _newPitch = newPitch;
    }

    public void Undo(BoardDocument document)
    {
        _item.Yaw = _oldYaw;
        _item.Pitch = _oldPitch;
    }

    public void Redo(BoardDocument document)
    {
        _item.Yaw = _newYaw;
        _item.Pitch = _newPitch;
    }
}

/// <summary>给三维几何体的某个面填色或清除填色。</summary>
public sealed class FillSolidFaceCommand : IBoardCommand
{
    private readonly SolidItem _item;
    private readonly int _face;
    private readonly uint? _oldColor;
    private readonly uint? _newColor;

    public FillSolidFaceCommand(SolidItem item, int face, uint? oldColor, uint? newColor)
    {
        _item = item;
        _face = face;
        _oldColor = oldColor;
        _newColor = newColor;
    }

    public void Undo(BoardDocument document) => _item.SetFaceColor(_face, _oldColor);

    public void Redo(BoardDocument document) => _item.SetFaceColor(_face, _newColor);
}
