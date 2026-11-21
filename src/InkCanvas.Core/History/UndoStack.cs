namespace InkCanvas.Core.History;

public interface IUndoableAction
{
    string Name { get; }
    void Apply();
    void Revert();
}

/// <summary>An undoable step built from two closures. The action is applied by the caller.</summary>
public sealed class DelegateAction : IUndoableAction
{
    private readonly Action _apply;
    private readonly Action _revert;

    public DelegateAction(string name, Action apply, Action revert)
    {
        Name = name;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _revert = revert ?? throw new ArgumentNullException(nameof(revert));
    }

    public string Name { get; }
    public void Apply() => _apply();
    public void Revert() => _revert();
}

/// <summary>
/// Bounded undo/redo history.
/// <para>
/// Convention: <see cref="Push"/> records an action that has <b>already been applied</b>,
/// which is how ink input works (the stroke is on screen before it is committed).
/// </para>
/// </summary>
public sealed class UndoStack
{
    private readonly List<IUndoableAction> _undo = new();
    private readonly List<IUndoableAction> _redo = new();

    public UndoStack(int capacity = 100)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public event Action Changed;

    public UndoHistoryState Capture() => new(_undo.ToArray(), _redo.ToArray());

    public void Restore(UndoHistoryState state)
    {
        _undo.Clear(); _redo.Clear();
        if (state is not null)
        {
            _undo.AddRange(state.Undo.TakeLast(Capacity));
            _redo.AddRange(state.Redo.TakeLast(Capacity));
        }
        Changed?.Invoke();
    }

    public void Push(IUndoableAction action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        _undo.Add(action);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var a = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        a.Revert();
        _redo.Add(a);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        var a = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        a.Apply();
        _undo.Add(a);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}

public sealed record UndoHistoryState(IReadOnlyList<IUndoableAction> Undo, IReadOnlyList<IUndoableAction> Redo);
