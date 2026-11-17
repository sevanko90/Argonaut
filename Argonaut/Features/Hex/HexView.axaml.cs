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
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Surface.Document = DataContext as HexViewModel;
        scrollBars.Refresh();
    }
}
