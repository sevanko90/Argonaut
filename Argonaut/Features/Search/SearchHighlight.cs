using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Argonaut.Features.Search;

/// <summary>
/// Attached properties that render a TextBlock's text with every occurrence of the current
/// find term drawn on a highlight background. Bind <see cref="TextProperty"/> instead of
/// TextBlock.Text, plus <see cref="TermProperty"/> to the view model's HighlightTerm;
/// <see cref="SuffixProperty"/> appends a literal (e.g. ": " after a property name) that is
/// never part of the searched text.
///
/// Highlighting is display-side re-matching, not byte-offset mapping: the row string is
/// decoded (and quoted) while search matches are raw byte offsets, so re-finding the term in
/// the displayed text is both simpler and reads better (all visible occurrences light up;
/// the current hit is distinguished by the row selection). When the term is null or absent
/// from the text, the plain Text fast path is used and no inlines are built.
/// </summary>
public sealed class SearchHighlight : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<SearchHighlight, TextBlock, string?>("Text");

    public static readonly AttachedProperty<string?> TermProperty =
        AvaloniaProperty.RegisterAttached<SearchHighlight, TextBlock, string?>("Term");

    public static readonly AttachedProperty<string?> SuffixProperty =
        AvaloniaProperty.RegisterAttached<SearchHighlight, TextBlock, string?>("Suffix");

    private const string HighlightBrushKey = "AppSearchHighlightBrush";

    static SearchHighlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Update(tb));
        TermProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Update(tb));
        SuffixProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Update(tb));
    }

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetTerm(TextBlock element) => element.GetValue(TermProperty);

    public static void SetTerm(TextBlock element, string? value) => element.SetValue(TermProperty, value);

    public static string? GetSuffix(TextBlock element) => element.GetValue(SuffixProperty);

    public static void SetSuffix(TextBlock element, string? value) => element.SetValue(SuffixProperty, value);

    private static void Update(TextBlock tb)
    {
        string text = GetText(tb) ?? string.Empty;
        string suffix = GetSuffix(tb) ?? string.Empty;

        var segments = SearchTextSplitter.Split(text, GetTerm(tb));
        if (segments is null)
        {
            tb.Inlines = null;
            tb.Text = suffix.Length == 0 ? text : text + suffix;
            return;
        }

        var inlines = new InlineCollection();
        foreach (var segment in segments)
        {
            var run = new Run(text.Substring(segment.Start, segment.Length));
            if (segment.IsMatch)
            {
                // DynamicResource-equivalent lookup so a live theme switch re-colors the
                // highlight, matching the app-wide convention in AppColors.axaml.
                run.Bind(TextElement.BackgroundProperty, tb.GetResourceObservable(HighlightBrushKey));
            }

            inlines.Add(run);
        }

        if (suffix.Length > 0)
            inlines.Add(new Run(suffix));

        tb.Inlines = inlines;
    }
}
