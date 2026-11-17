using System;
using Argonaut.Ui.Rows;
using Avalonia.Controls;

namespace Argonaut.Features.Hex;

/// <summary>A document as a hex dump - see <see cref="HexViewModel"/> and <see cref="HexSurface"/>.</summary>
public partial class HexView : UserControl
{
    private readonly RowScrollBars scrollBars;

    public HexView()
    {
        InitializeComponent();
        scrollBars = new RowScrollBars(Surface, VerticalScrollBar, PanScrollBar);
        DetachedFromVisualTree += (_, _) => scrollBars.Dispose();

        // The dump is the document, so it takes focus when the document is shown: the caret
        // shows solid and the keys reach it, rather than focus landing on the window's chrome.
        // Nothing else has been focused yet at load time, so this takes focus from nobody.
        Loaded += (_, _) => Surface.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Surface.Document = DataContext as HexViewModel;
        scrollBars.Refresh();
    }
}
