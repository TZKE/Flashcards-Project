using AIFlashcardMaker.ChartsStudio.Domain.Specs;

namespace AIFlashcardMaker.ChartsStudio.Application.Figures;

/// <summary>
/// Charts Studio Phase 3 — undo/redo over a figure's patch.
///
/// SNAPSHOTS, NOT COMMANDS. A patch is a small value object, so history is a stack of cloned
/// patch states. Undo/redo is then trivially deterministic — restore a snapshot — with none of
/// the classic command-pattern failure modes (a command whose Undo() doesn't quite mirror its
/// Do(), drift after replay, ordering bugs). "Deterministic replay" is free because there is
/// nothing to replay: every state is complete.
///
/// Guarantees:
///   • No duplicated states — committing a state key-equal to the current one is a no-op.
///   • Redo clears on a new commit, as every editor a user has ever met behaves.
///   • Unlimited depth — patches are tens of bytes; memory is a non-issue at editing scale.
///   • Dirty is a comparison against a saved-point marker, not a boolean that drifts.
///
/// Not thread-safe by design: history belongs to one editor on the UI thread. The render queue
/// handles all cross-thread concerns.
/// </summary>
public sealed class PatchHistory
{
    private readonly List<FigurePatch?> _undo = new();
    private readonly List<FigurePatch?> _redo = new();
    private string _savedKey;

    public PatchHistory(FigurePatch? initial)
    {
        Current = FigurePatch.Canonicalize(initial)?.Clone();
        _savedKey = FigurePatch.KeyOf(Current);
    }

    /// <summary>The state being edited. Null = no overrides. Treat as immutable: to change it,
    /// build a new patch and Commit — mutating this directly would corrupt the snapshots.</summary>
    public FigurePatch? Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Depth counts for the UI ("Undo (3)") and for tests.</summary>
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>True when Current differs from the last saved state.</summary>
    public bool IsDirty => !string.Equals(FigurePatch.KeyOf(Current), _savedKey, StringComparison.Ordinal);

    /// <summary>
    /// Commits a new state. Returns false (and records nothing) when the state is key-equal
    /// to the current one — a slider wiggled back to its value, a title retyped identically —
    /// so the undo stack only ever contains real changes.
    /// </summary>
    public bool Commit(FigurePatch? next)
    {
        next = FigurePatch.Canonicalize(next);

        if (string.Equals(FigurePatch.KeyOf(next), FigurePatch.KeyOf(Current), StringComparison.Ordinal))
            return false;

        _undo.Add(Current);
        Current = next?.Clone();
        _redo.Clear();
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;

        _redo.Add(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;

        _undo.Add(Current);
        Current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    /// <summary>Marks the current state as saved; dirty is measured against this point from
    /// now on. Undoing PAST the saved point correctly reads as dirty again.</summary>
    public void MarkSaved() => _savedKey = FigurePatch.KeyOf(Current);
}
