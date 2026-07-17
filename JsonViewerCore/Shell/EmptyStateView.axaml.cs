using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace JsonViewerCore.Shell;

public partial class EmptyStateView : UserControl
{
    public event EventHandler? ChooseFileRequested;
    public event EventHandler? ClearRecentFilesRequested;

    public EmptyStateView()
    {
        InitializeComponent();
    }

    public StackPanel RecentFilesHost => RecentFilesPanel;

    public bool ClearRecentFilesButtonVisible
    {
        get => ClearRecentFilesButton.IsVisible;
        set => ClearRecentFilesButton.IsVisible = value;
    }

    private void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        ChooseFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearRecentFiles(object? sender, RoutedEventArgs e)
    {
        ClearRecentFilesRequested?.Invoke(this, EventArgs.Empty);
    }
}
