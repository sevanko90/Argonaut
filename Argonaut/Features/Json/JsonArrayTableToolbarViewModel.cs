using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>One entry in the column-mode picker.</summary>
/// <param name="DisplayName">Label shown in the dropdown.</param>
/// <param name="Mode">Which layout this entry selects.</param>
/// <param name="Columns">Column count for <see cref="JsonArrayColumnMode.Reshape"/>; ignored
/// for by-property, where the discovered property names decide the count.</param>
public sealed record JsonArrayColumnModeOption(string DisplayName, JsonArrayColumnMode Mode, int Columns);

/// <summary>
/// The array-table document's header toolbar, reached through the existing
/// <see cref="Argonaut.Shell.IDocumentViewModel.Toolbar"/> seam: where this table came from, the
/// column-mode picker, and the way back to the JSON tree.
///
/// <b>The picker is a plain ComboBox, not a self-closing flyout.</b> Its selection setter runs
/// inside Avalonia's still-open selection commit (see CLAUDE.md), and the Reset it causes is safe
/// ONLY because that Reset lands on the grid's collection, never on this picker's own
/// ItemsSource - which is a fixed list built once, here. Put the same setter behind a flyout that
/// closes on pick and it is the SchemaRootPickerViewModel crash verbatim, at which point it owes
/// UiDeferral.AfterCurrentInput.
///
/// Back is injected as a callback that already captured the origin path, so it does not run
/// against this view model at all - see JsonArrayTableViewModel's remarks on the document being
/// disposed partway through its own Back.
/// </summary>
public sealed class JsonArrayTableToolbarViewModel : ObservableObject
{
    /// <summary>Reshape widths offered. Small on purpose: the mode exists for interleaved
    /// coordinates and fixed-width records, where the useful widths are 2, 3 and 4.</summary>
    private const int MaxReshapeColumns = 5;

    private readonly Action<JsonArrayColumnModeOption> setColumnMode;
    private readonly Func<Task> back;
    private JsonArrayColumnModeOption selectedColumnMode;

    public JsonArrayTableToolbarViewModel(string originPath, string originFilePath,
        Action<JsonArrayColumnModeOption> setColumnMode, Func<Task> back)
    {
        this.setColumnMode = setColumnMode;
        this.back = back;

        OriginPath = originPath;
        OriginFileName = Path.GetFileName(originFilePath);

        var options = new List<JsonArrayColumnModeOption>(MaxReshapeColumns + 1)
        {
            new("By property", JsonArrayColumnMode.ByProperty, 0)
        };
        for (int n = 1; n <= MaxReshapeColumns; n++)
            options.Add(new JsonArrayColumnModeOption(n == 1 ? "1 column" : $"{n} columns", JsonArrayColumnMode.Reshape, n));

        ColumnModes = options;
        selectedColumnMode = options[0];
    }

    /// <summary>The JSONPath this array sits at in the origin document.</summary>
    public string OriginPath { get; }

    /// <summary>File name of the origin document, for the banner.</summary>
    public string OriginFileName { get; }

    /// <summary>Banner text: where this table came from, in one line.</summary>
    public string OriginDescription => $"Table view of {OriginPath} in {OriginFileName}";

    public IReadOnlyList<JsonArrayColumnModeOption> ColumnModes { get; }

    public JsonArrayColumnModeOption SelectedColumnMode
    {
        get => selectedColumnMode;
        set
        {
            if (value is null || !SetField(ref selectedColumnMode, value))
                return;

            setColumnMode(value);
        }
    }

    /// <summary>Reloads the origin file as JSON and reveals the path this table came from.</summary>
    public Task BackAsync() => back();
}
