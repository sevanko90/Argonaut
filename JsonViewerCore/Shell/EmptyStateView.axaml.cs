using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace JsonViewerCore.Shell;

public partial class EmptyStateView : UserControl
{
    public event EventHandler? ChooseFileRequested;

    public EmptyStateView()
    {
        InitializeComponent();
    }

    public StackPanel RecentFilesHost => RecentFilesPanel;

    private void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        ChooseFileRequested?.Invoke(this, EventArgs.Empty);
    }
}
