using System;
using Argonaut.Engine.Http;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Requests;

/// <summary>
/// One row of the request pane's argument list: a key and a value, decoded. A row parsed from the
/// URL keeps the text it had there until it is edited, so the URL is rewritten only for what the
/// user changed. A row with no key and no value is not an argument yet - a row just added.
/// </summary>
public sealed class QueryArgumentRowViewModel : ObservableObject
{
    private readonly Action<QueryArgumentRowViewModel> changed;
    private string key;
    private string value;

    // The row's text in the URL, while it is as parsed; null once edited, or for a new row.
    private string? raw;

    // A parsed bare flag (no "=") stays one until its value is edited.
    private bool parsedAsFlag;

    internal QueryArgumentRowViewModel(QueryPart? parsed, Action<QueryArgumentRowViewModel> changed)
    {
        this.changed = changed;
        key = parsed?.Argument.Key ?? string.Empty;
        value = parsed?.Argument.Value ?? string.Empty;
        raw = parsed?.Raw;
        parsedAsFlag = parsed is { Argument.Value: null };
    }

    public string Key
    {
        get => key;
        set => Edit(ref key, value ?? string.Empty);
    }

    public string Value
    {
        get => value;
        set
        {
            parsedAsFlag = false;
            Edit(ref this.value, value ?? string.Empty);
        }
    }

    /// <summary>No key and no value: a row the user has added but not filled in.</summary>
    public bool IsEmpty => key.Length == 0 && value.Length == 0;

    /// <summary>What the URL holds for this row - as parsed while unedited, otherwise encoded
    /// anew, with an empty value written as a bare flag.</summary>
    internal QueryPart Part
        => raw is not null
            ? new QueryPart(new QueryArgument(key, parsedAsFlag ? null : value), raw)
            : UrlQuery.Part(new QueryArgument(key, value.Length == 0 ? null : value));

    private void Edit(ref string field, string next)
    {
        if (!SetField(ref field, next))
            return;

        raw = null;
        OnPropertyChanged(nameof(IsEmpty));
        changed(this);
    }
}
