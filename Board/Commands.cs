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
