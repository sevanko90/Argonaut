using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Tree;

/// <summary>
/// What a JSON tree row says: a member's name, then its value - a scalar coloured by type, or a
/// container's bracket or collapsed summary - then any notes: a decoded date, a truncation notice,
/// a "View as table" action. An array element's index is its marker.
/// </summary>
public sealed class JsonTreePainter(JsonTreeText text, IReadOnlyList<IValueHintProvider>? hintProviders, bool offerArrayTable) : ITreeRowPainter
{
    public void AppendRuns(in TreeRow row, List<TreeRun> runs)
    {
        bool isObject = row.Node.FormatKind == (byte)JsonTokenKind.StartObject;
        if (row.Shape == TreeRowShape.Close)
        {
            runs.Add(new TreeRun(isObject ? "}" : "]", TreeRunStyle.Plain));
            return;
        }

        string? name = text.Name(row, out bool nameTruncated, out long nameLength);
        if (name is not null)
            runs.Add(new TreeRun(name + ": ", TreeRunStyle.Name));

        if (row.Shape == TreeRowShape.Open)
        {
            runs.Add(new TreeRun(text.Summary(row), TreeRunStyle.Plain));
            if (nameTruncated)
                runs.Add(new TreeRun(NameTruncatedNote(nameLength), TreeRunStyle.Note));
            if (offerArrayTable && !isObject && text.HasChildren(row.Node.ValueStart, row.Node.FormatKind))
                runs.Add(new TreeRun("View as table", TreeRunStyle.Action, new ViewAsTableLink(row.Node.ValueStart), TreeRunIcon.Table));
            return;
        }

        var kind = (JsonTokenKind)row.Node.FormatKind;
        string value = text.Scalar(row, out bool valueTruncated, out long contentOffset, out long valueLength);
        var hint = Hint(row, kind);
        runs.Add(new TreeRun(value, StyleOf(kind), hint?.ValueLink));

        // Notes, then chips, then actions: information first, and what appears on hover last, so
        // showing it moves nothing.
        if (nameTruncated)
            runs.Add(new TreeRun(NameTruncatedNote(nameLength), TreeRunStyle.Note));
        if (valueTruncated)
            runs.Add(new TreeRun(ByteLengthText.Format(valueLength), TreeRunStyle.Note));

        if (hint is not null)
            runs.Add(new TreeRun(hint.Text, hint.Style, hint.Link, hint.Icon, hint.Swatch));

        if (valueTruncated)
            runs.Add(new TreeRun("Open in raw", TreeRunStyle.Action, new ViewInRawLink(contentOffset), TreeRunIcon.FullText));
    }

    public string? Marker(in TreeRow row)
        => row.ParentKind == (byte)JsonTokenKind.StartArray && row.Shape != TreeRowShape.Close ? row.Ordinal.ToString() : null;

    private ValueHint? Hint(in TreeRow row, JsonTokenKind kind)
    {
        if (hintProviders is null)
            return null;

        // Most hints read whole values, which are never near the display cap; past it, only the
        // ones that classify from a prefix (Base64, an embedded document) are asked.
        var raw = text.ScalarBytes(row, DisplayText.MaxLength, out long length);
        if (raw.IsEmpty)
            return null;

        bool isWhole = length == raw.Length;
        foreach (var provider in hintProviders)
        {
            if (provider.IsActive && (isWhole || provider.ReadsPrefixes) && provider.TryClassify(kind, raw, length, out var candidate)
                && provider.FormatHint(in candidate, raw, length, row.Node.ValueStart) is { } hint)
                return hint;
        }

        return null;
    }

    private static TreeRunStyle StyleOf(JsonTokenKind kind) => kind switch
    {
        JsonTokenKind.String => TreeRunStyle.String,
        JsonTokenKind.Number => TreeRunStyle.Number,
        JsonTokenKind.True or JsonTokenKind.False => TreeRunStyle.Keyword,
        JsonTokenKind.Null => TreeRunStyle.Literal,
        _ => TreeRunStyle.Plain,
    };

    private static string NameTruncatedNote(long length) => $"name truncated, full length {ByteLengthText.Format(length)}";

}
