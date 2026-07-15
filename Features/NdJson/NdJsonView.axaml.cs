using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace JsonViewerCore.Features.NdJson;

public partial class NdJsonView : UserControl
{
    public NdJsonView()
    {
        InitializeComponent();
        
        this.DetachedFromVisualTree += (_, __) =>
        {
            if (DataContext is IDisposable d)
                d.Dispose();
        };
        
        this.AttachedToVisualTree += (_, __) =>
        {
            // Force template creation
            JsonLinesListBox.ApplyTemplate();

            // Find internal ScrollViewer
            var sv = JsonLinesListBox.GetVisualDescendants()
                                     .OfType<ScrollViewer>()
                                     .FirstOrDefault();

            if (sv != null)
            {
                sv.ScrollChanged += (_, e) =>
                {
                    
                    var vm = (NdJsonViewModel)DataContext;

                    double lineHeight = 20;
                    int firstVisibleIndex = (int)(e.ViewportDelta.Y / lineHeight);

                    vm.LoadWindow(firstVisibleIndex, 50);
                };
            }
        };
    }
}