using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell;

/// <summary>One row in the recent-files list: full path plus the file name shown on the button.</summary>
public sealed record RecentFileItem(string Path, string FileName);

public partial class EmptyStateView : UserControl
{
    public event EventHandler? ChooseFileRequested;
    public event EventHandler? ClearRecentFilesRequested;
    public event EventHandler<string>? OpenRecentFileRequested;

    public EmptyStateView()
    {
        InitializeComponent();
    }

    /// <summary>Binds the recent-files list and toggles the empty-state text / clear button.</summary>
    public void SetRecentFiles(IReadOnlyList<RecentFileItem> items)
    {
        RecentFilesList.ItemsSource = items;
        NoRecentFilesText.IsVisible = items.Count == 0;
        ClearRecentFilesButton.IsVisible = items.Count > 0;
    }

    private void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        ChooseFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearRecentFiles(object? sender, RoutedEventArgs e)
    {
        ClearRecentFilesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecentFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is RecentFileItem item)
            OpenRecentFileRequested?.Invoke(this, item.Path);
    }
}
