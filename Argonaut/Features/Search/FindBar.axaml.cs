using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace Argonaut.Features.Search;

/// <summary>
/// The find bar hosted by MainWindow: term box, previous/next, "n of m" status, close.
/// Purely a view - it raises <see cref="FindRequested"/>/<see cref="CloseRequested"/> and
/// leaves all search behavior to <see cref="FindController"/>.
/// </summary>
public partial class FindBar : UserControl
{
    /// <summary>Raised on Enter or the prev/next buttons; direction is +1 forward, -1 backward.</summary>
    public event Action<string, int>? FindRequested;

    public event Action? CloseRequested;

    public FindBar()
    {
        InitializeComponent();

        IsVisible = false;
        TermBox.KeyDown += OnTermBoxKeyDown;
        PreviousButton.Click += (_, _) => RequestFind(-1);
        NextButton.Click += (_, _) => RequestFind(1);
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
    }

    public void Open()
    {
        IsVisible = true;
        TermBox.Focus();
        TermBox.SelectAll();
    }

    public void Close()
    {
        IsVisible = false;
        SetStatus(null);
    }

    public void SetStatus(string? status)
    {
        StatusLabel.Text = status ?? string.Empty;
    }

    public void RequestFind(int direction)
    {
        var term = TermBox.Text;
        if (!string.IsNullOrEmpty(term))
            FindRequested?.Invoke(term, direction);
    }

    private void OnTermBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        RequestFind((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
        e.Handled = true;
    }
}
