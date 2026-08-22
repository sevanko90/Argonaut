using Avalonia;
using Avalonia.Controls;

namespace Argonaut.Features.Json;

/// <summary>See JsonRowPresenter.axaml - the shared read-only row content.</summary>
public partial class JsonRowPresenter : UserControl
{
    /// <summary>
    /// Find term to highlight in this row's name and value, or null for none. A property on
    /// the presenter rather than a binding reaching up to a specific view model, because the
    /// two hosts name it in different places: the JSON view has its own inline template, while
    /// the diff binds both panes to <c>JsonDiffViewModel.HighlightTerm</c>.
    /// </summary>
    public static readonly StyledProperty<string?> HighlightTermProperty =
        AvaloniaProperty.Register<JsonRowPresenter, string?>(nameof(HighlightTerm));

    public string? HighlightTerm
    {
        get => GetValue(HighlightTermProperty);
        set => SetValue(HighlightTermProperty, value);
    }

    public JsonRowPresenter()
    {
        InitializeComponent();
    }
}
