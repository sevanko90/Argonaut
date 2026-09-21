using System;
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
            $"row-index budget {N(state.TotalDerivedRows)} / {N(state.MaxDerivedRows)} rows "
            + $"({state.BudgetUsed * 100:0.00}%) across {state.SpanCount} span(s)"
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
            return "  none - the document still reads exactly as the file does.";

        var text = new StringBuilder();
        text.AppendLine("    #  anchor  orig row   start row  line   rows held  converged  reach     bytes                    own Δb/Δr/Δl      before Δb/Δr/Δl");

        foreach (var span in state.Spans)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {span.Index,3}  {span.OriginalAnchor,6}  {span.OriginalStartRow,8}  {span.StartRow,10}  "
                + $"{(span.FirstLineNumber is int line ? line.ToString("N0", CultureInfo.InvariantCulture) : "—"),5}  "
                + $"{span.RowsHeld,9}  {span.ConvergedOriginalRow,9}  {span.EditReach,7}  "
                + $"{N(span.StartOffset) + ".." + N(span.EndOffset),-22}  "
                + $"{Signed(span.ByteDelta) + "/" + Signed(span.RowDelta) + "/" + Signed(span.LineDelta),-16}  "
                + $"{Signed(span.ByteDeltaBefore) + "/" + Signed(span.RowDeltaBefore) + "/" + Signed(span.LineDeltaBefore)}"));
        }

        return text.ToString().TrimEnd();
    }

    private static string DescribePieces(RawEditSnapshot state)
    {
        var text = new StringBuilder();
        text.AppendLine("    #  buffer          buffer offset        length   logical start");

        foreach (var piece in state.Pieces)
        {
            string buffer = piece.FromOriginal ? "original" : $"scratch #{piece.ScratchChunk}";
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {piece.Index,3}  {buffer,-14}  {N(piece.BufferOffset),13}  {N(piece.Length),12}  {N(piece.LogicalStart),14}"));
        }

        if (state.PiecesOmitted > 0)
            text.AppendLine($"  … {N(state.PiecesOmitted)} more");

        return text.ToString().TrimEnd();
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
