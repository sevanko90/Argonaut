using System;
using System.Collections.Generic;
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
/// <b>The picker only exists for an array of scalars.</b> An array of objects already has its
/// columns named by the data - the property names ARE the header - so re-widthing it into N
/// generic columns can only make it worse: every cell becomes a container summary and every
/// column collapses to the minimum width. So an object array is built with
/// <c>canReshape: false</c>, which offers by-property alone and hides the picker entirely.
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
    private readonly Action<int> setArrayColumns;
    private readonly Func<Task> back;
    private JsonArrayColumnModeOption selectedColumnMode;
    private int selectedArrayColumns = JsonArrayColumnDiscovery.DefaultArrayColumns;
    private bool canExpandArrays;

    public JsonArrayTableToolbarViewModel(string arrayPath, bool canReshape,
        Action<JsonArrayColumnModeOption> setColumnMode, Action<int> setArrayColumns, Func<Task> back)
    {
        this.setColumnMode = setColumnMode;
        this.setArrayColumns = setArrayColumns;
        this.back = back;

        ArrayPath = arrayPath;

        CanReshape = canReshape;

        var options = new List<JsonArrayColumnModeOption>(canReshape ? MaxReshapeColumns + 1 : 1)
        {
            new("By property", JsonArrayColumnMode.ByProperty, 0)
        };
        if (canReshape)
        {
            for (int n = 1; n <= MaxReshapeColumns; n++)
                options.Add(new JsonArrayColumnModeOption(n == 1 ? "1 column" : $"{n} columns", JsonArrayColumnMode.Reshape, n));
        }

        ColumnModes = options;
        selectedColumnMode = options[0];

        var widths = new int[JsonArrayColumnDiscovery.MaxArrayColumns];
        for (int n = 1; n <= widths.Length; n++)
            widths[n - 1] = n;

        ArrayColumnCounts = widths;
    }

    /// <summary>The JSONPath this array sits at in the origin document.</summary>
    public string ArrayPath { get; }

    /// <summary>Banner text identifying the array in the origin document.</summary>
    public string OriginDescription => $"Table view of {ArrayPath}";

    /// <summary>Whether the column-mode picker is shown at all. False for an array of objects,
    /// whose columns are the property names by definition - the by-property entry is then the
    /// only mode, and a one-item dropdown is noise.</summary>
    public bool CanReshape { get; }

    /// <summary>The offered modes. By-property alone unless <see cref="CanReshape"/>.</summary>
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

    /// <summary>How many positions an expanded array column draws. Small on purpose - past a
    /// handful the reader wants the cell pane, not more columns.</summary>
    public IReadOnlyList<int> ArrayColumnCounts { get; }

    public int SelectedArrayColumns
    {
        get => selectedArrayColumns;
        set
        {
            if (value <= 0 || !SetField(ref selectedArrayColumns, value))
                return;

            setArrayColumns(value);
        }
    }

    /// <summary>
    /// Whether the array-width picker is shown. Answered by the document after every discovery
    /// rather than once at construction: expanding an object column can reveal an array nobody
    /// could see when the table opened.
    /// </summary>
    public bool CanExpandArrays
    {
        get => canExpandArrays;
        private set => SetField(ref canExpandArrays, value);
    }

    /// <summary>Called by the document with what its freshly discovered columns hold.</summary>
    public void ShowArrayColumns(bool hasArrayColumns) => CanExpandArrays = hasArrayColumns;

    /// <summary>Reloads the origin file as JSON and reveals the path this table came from.</summary>
    public Task BackAsync() => back();
}
