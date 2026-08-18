namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Display model for one visible diff row: both sides' <see cref="JsonRow"/>s (either may
/// be null when that side is absent), the diff status driving the row tint, and the merged
/// depth driving the indent (the per-side <see cref="JsonRow.Depth"/> is deliberately NOT
/// used - alignment is the whole point). Placeholder rows mark a display cap, exactly like
/// the JSON view's.
/// </summary>
public sealed class JsonDiffRow
{
    public JsonDiffRow(int position, JsonRow? left, JsonRow? right, DiffStatus status, int depth,
        bool hasChildren, bool isExpanded, bool isPlaceholder, string? moveBadge = null, string? note = null, string? placeholderText = null,
        bool isValueChanged = false, bool isChangedPath = false)
    {
        Position = position;
        Left = left;
        Right = right;
        Status = status;
        Depth = depth;
        HasChildren = hasChildren;
        IsExpanded = isExpanded;
        IsPlaceholder = isPlaceholder;
        MoveBadge = moveBadge;
        Note = note;
        PlaceholderText = placeholderText;
        IsValueChanged = isValueChanged;
        IsChangedPath = isChangedPath;
    }

    /// <summary>Index into the owning collection's current visible list.</summary>
    public int Position { get; }

    public JsonRow? Left { get; }

    public JsonRow? Right { get; }

    public DiffStatus Status { get; }

    /// <summary>Merged-tree depth - one indent for both panes, which is what keeps them aligned.</summary>
    public int Depth { get; }

    public bool HasChildren { get; }

    public bool IsExpanded { get; }

    public bool IsPlaceholder { get; }

    /// <summary>"moved from [2]", "moved from /config/db", or "moved to /meta/db →"; null on non-moved rows.</summary>
    public string? MoveBadge { get; }

    /// <summary>Muted annotation, e.g. "alignment approximate" on an over-cap array.</summary>
    public string? Note { get; }

    /// <summary>The display-cap placeholder's text; null on real rows.</summary>
    public string? PlaceholderText { get; }

    /// <summary>A Modified row whose VALUE is the change (a leaf, or an undescended
    /// approximate container) - the actual data difference. Drives the strong highlight
    /// on the value text itself, distinct from the whole-row path tint.</summary>
    public bool IsValueChanged { get; }

    /// <summary>A Modified container that was descended into - not itself a change, but on
    /// the path to one. Drives the faint "open me" tint that guides the eye down the tree.</summary>
    public bool IsChangedPath { get; }

    // Classes.* bindings for the row tint.
    public bool IsAdded => Status == DiffStatus.Added;
    public bool IsRemoved => Status == DiffStatus.Removed;
    public bool IsModified => Status == DiffStatus.Modified;
    public bool IsMoved => Status == DiffStatus.Moved;

    public bool HasLeft => Left is not null;
    public bool HasRight => Right is not null;
    public bool HasMoveBadge => MoveBadge is not null;
    public bool HasNote => Note is not null;
}
