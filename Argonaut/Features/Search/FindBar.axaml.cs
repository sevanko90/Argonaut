using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace Argonaut.Features.Search;

/// <summary>
/// The find controls hosted inline in MainWindow's toolbar: term box, previous/next,
/// "n of m" status, clear. Purely a view - it raises <see cref="FindRequested"/>/
/// <see cref="ResetRequested"/> and leaves all search behavior to <see cref="FindController"/>.
/// </summary>
public partial class FindBar : UserControl
{
    /// <summary>Raised on Enter or the prev/next buttons; direction is +1 forward, -1 backward.</summary>
    public event Action<string, int>? FindRequested;

    /// <summary>Raised by the clear button; asks the owner to stop the active search and reset.</summary>
    public event Action? ResetRequested;

    public FindBar()
    {
        InitializeComponent();

        TermBox.KeyDown += OnTermBoxKeyDown;
        PreviousButton.Click += (_, _) => RequestFind(-1);
        NextButton.Click += (_, _) => RequestFind(1);
        CloseButton.Click += (_, _) => ResetRequested?.Invoke();
    }

    /// <summary>Ctrl+F target: focuses the term box and selects its contents.</summary>
    public void FocusTerm()
    {
        TermBox.Focus();
        TermBox.SelectAll();
    }

    /// <summary>Clears the term/status, e.g. after Escape, the clear button, or a file close/switch.</summary>
    public void Reset()
    {
        TermBox.Text = string.Empty;
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
