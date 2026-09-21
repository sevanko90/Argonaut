using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Argonaut.Features.Raw;

namespace Argonaut.Diagnostics;

/// <summary>
/// A window onto the raw editor's internals, for development only - the whole
/// <c>Diagnostics</c> folder is excluded from the build outside Debug (see Argonaut.csproj), so
/// there is nothing here to ship or to hide behind a setting.
///
/// It exists because the two structures that make multi-GB editing possible are the two a reader
/// cannot see. A piece table means the bytes on screen are assembled from runs of two different
/// buffers, and a dirty span is a region of the row index rather than anything drawn - so
/// "300 rows held over 2 spans, 14 bytes of scratch, budget 0.4%" is not inferable from the
/// document at all. Watching those numbers move per keystroke is the fastest way to build an
/// intuition for what an edit actually costs, and the fastest way to notice when it costs
/// something absurd.
///
/// Built in code rather than XAML on purpose: a .axaml file would have to be excluded from
/// AvaloniaResource as well as from compilation, and a developer tool is not worth two exclusion
/// rules that can disagree.
/// </summary>
internal sealed class RawEditInspectorWindow : Window
{
    private readonly RawViewModel document;
    private readonly RawEditMapView map = new();
    private readonly TextBlock headline = Mono(13);
    private readonly TextBlock budget = Mono(12);
    private readonly TextBlock spans = Mono(11);
    private readonly TextBlock pieces = Mono(11);
    private readonly TextBlock journal = Mono(12);
    private readonly ProgressBar budgetBar = new() { Minimum = 0, Maximum = 1000, Height = 6 };

    public RawEditInspectorWindow(RawViewModel document)
    {
        this.document = document;

        Title = "Argonaut internals — raw editor";
        Width = 900;
        Height = 700;
        this[!BackgroundProperty] = new DynamicResourceExtension("AppWindowBackgroundBrush");

        this.map[!ForegroundProperty] = new DynamicResourceExtension("AppMutedTextBrush");

        Content = new ScrollViewer
        {
            Padding = new Thickness(16),
            Content = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    this.headline,
                    new StackPanel { Spacing = 4, Children = { this.budget, this.budgetBar } },
                    this.map,
                    Section("Dirty spans", this.spans),
                    Section("Piece table", this.pieces),
                    this.journal,
                }
            }
        };

        this.document.PropertyChanged += OnDocumentChanged;
        Refresh();
    }

    /// <summary>
    /// Stops following the document. Called when the document is closed or switched away from -
    /// a snapshot copies everything it shows, so what is on screen stays readable, it simply
    /// stops changing.
    /// </summary>
    public void Detach() => this.document.PropertyChanged -= OnDocumentChanged;

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (this.document.Editor is not { } editor)
        {
            this.headline.Text = "Not editing. Turn on Edit in the toolbar - there is no piece table until then.";
            this.budget.Text = string.Empty;
            this.spans.Text = string.Empty;
            this.pieces.Text = string.Empty;
            this.journal.Text = string.Empty;
            this.map.Snapshot = null;
            return;
        }

        var state = editor.Describe();
        this.map.Snapshot = state;

        this.headline.Text =
            $"{N(state.DocumentLength)} bytes ({Signed(state.ByteDelta)} vs file)    "
            + $"{N(state.RowCount)} rows ({Signed(state.RowDelta)})    "
            + $"{N(state.PieceCount)} pieces    "
            + $"{N(state.ScratchBytesUsed)} B scratch in {state.ScratchChunkCount} chunk(s)    "
            + (state.IsUnedited ? "unedited" : "edited");

        this.budget.Text =
            $"row-index budget {N(state.HeldAnchors)} / {N(state.MaxHeldAnchors)} anchors "
            + $"({state.BudgetUsed * 100:0.00}%) covering {N(state.TotalDerivedRows)} rows "
            + $"across {state.SpanCount} span(s)"
            + (state.NeedsRebuild ? "    NEEDS REBUILD - new edit sites are being refused" : string.Empty);
        this.budgetBar.Value = Math.Clamp(state.BudgetUsed * 1000, 0, 1000);

        this.spans.Text = DescribeSpans(state);
        this.pieces.Text = DescribePieces(state);

        string selection = state.SelectionEnd > state.SelectionStart
            ? $"    selection {N(state.SelectionStart)}..{N(state.SelectionEnd)}"
            : string.Empty;
        this.journal.Text =
            $"undo depth {state.UndoDepth}  (undo {YesNo(state.CanUndo)}, redo {YesNo(state.CanRedo)})"
            + $"    caret {N(state.CaretOffset)}{selection}";
    }

    /// <summary>
    /// One line per span. The columns are chosen to make the invariants checkable by eye: spans
    /// are ordered and disjoint, so each row's <c>bytes</c> range must start at or after the
    /// previous row's end; and <c>own</c> against <c>before</c> is what displaces everything
    /// after the span, so the last row's two added together is the whole document's delta.
    /// </summary>
    private static string DescribeSpans(RawEditSnapshot state)
    {
        if (state.SpanCount == 0)
            return "none - the document still reads exactly as the file does.";

        var rows = new List<string[]>(state.SpanCount);
        foreach (var span in state.Spans)
        {
            rows.Add(new[]
            {
                span.Index.ToString(CultureInfo.InvariantCulture),
                N(span.OriginalAnchor),
                N(span.OriginalStartRow),
                N(span.StartRow),
                span.FirstLineNumber is int line ? N(line) : "—",
                N(span.RowsHeld),
                N(span.AnchorsHeld),
                N(span.ConvergedOriginalRow),
                N(span.EditReach),
                $"{N(span.StartOffset)}..{N(span.EndOffset)}",
                $"{Signed(span.ByteDelta)}/{Signed(span.RowDelta)}/{Signed(span.LineDelta)}",
                $"{Signed(span.ByteDeltaBefore)}/{Signed(span.RowDeltaBefore)}/{Signed(span.LineDeltaBefore)}",
            });
        }

        return Table(
            new[] { "#", "anchor", "orig row", "start row", "line", "rows", "anchors", "converged", "reach", "bytes", "own Δb/Δr/Δl", "before Δb/Δr/Δl" },
            rows);
    }

    private static string DescribePieces(RawEditSnapshot state)
    {
        var rows = new List<string[]>(state.Pieces.Count);
        foreach (var piece in state.Pieces)
        {
            rows.Add(new[]
            {
                piece.Index.ToString(CultureInfo.InvariantCulture),
                piece.FromOriginal ? "original" : $"scratch #{piece.ScratchChunk}",
                N(piece.BufferOffset),
                N(piece.Length),
                N(piece.LogicalStart),
            });
        }

        string table = Table(new[] { "#", "buffer", "buffer offset", "length", "logical start" }, rows);
        return state.PiecesOmitted > 0 ? $"{table}\n… {N(state.PiecesOmitted)} more" : table;
    }

    /// <summary>
    /// Lays a table out by measuring it. The first cut wrote the header as a literal and padded
    /// the cells to guessed widths, which lined up until the first nine-digit byte offset and
    /// then never again - on the document this window exists for, every number is nine digits.
    /// Columns are right-aligned except the one text column, since these are all quantities and
    /// a ragged right edge is what makes a column of numbers unreadable.
    /// </summary>
    private static string Table(string[] headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (int column = 0; column < headers.Length; column++)
        {
            widths[column] = headers[column].Length;
            foreach (var row in rows)
                widths[column] = Math.Max(widths[column], row[column].Length);
        }

        var text = new StringBuilder();
        AppendRow(text, headers, widths);
        foreach (var row in rows)
            AppendRow(text, row, widths);

        return text.ToString().TrimEnd();
    }

    private static void AppendRow(StringBuilder text, string[] cells, int[] widths)
    {
        for (int column = 0; column < cells.Length; column++)
        {
            if (column > 0)
                text.Append("  ");

            // The buffer column is a name, not a quantity, so it reads left-aligned; everything
            // else lines up on its last digit.
            bool leftAlign = cells[column].StartsWith("original", StringComparison.Ordinal)
                             || cells[column].StartsWith("scratch", StringComparison.Ordinal)
                             || cells[column] == "buffer";

            text.Append(leftAlign ? cells[column].PadRight(widths[column]) : cells[column].PadLeft(widths[column]));
        }

        text.AppendLine();
    }

    private static Control Section(string title, TextBlock body)
        => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12 },
                new Border
                {
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(4),
                    [!Border.BackgroundProperty] = new DynamicResourceExtension("AppPanelBackgroundBrush"),
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        MaxHeight = 220,
                        Content = body,
                    }
                }
            }
        };

    private static TextBlock Mono(double size)
    {
        var block = new TextBlock
        {
            FontFamily = FontFamily.Parse("Menlo,Consolas,DejaVu Sans Mono,monospace"),
            FontSize = size,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        block[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AppTextBrush");
        return block;
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Signed(long value) => value.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";
}
