using System;
using System.Collections.Generic;
using Argonaut.Engine.Locations;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell;

/// <summary>One row in the recent or favourite list: full path, the file name shown on the button,
/// and whether the file is pinned (which fills its star).</summary>
public sealed record RecentFileItem(string Path, string FileName, bool IsFavourite = false);

public partial class EmptyStateView : UserControl
{
    public event EventHandler? ChooseFileRequested;
    public event EventHandler? PasteRequested;
    public event EventHandler? ClearRecentFilesRequested;
    public event EventHandler<string>? OpenRecentFileRequested;
    public event EventHandler<string>? ToggleFavouriteRequested;

    /// <summary>Raised with a folder the user chose to browse from.</summary>
    public event EventHandler<string>? BrowseFromFolderRequested;

    /// <summary>Raised as the locations menu opens, so the shell can supply a current list.</summary>
    public event EventHandler? LocationsRequested;

    private readonly MenuFlyout locationsMenu = new();

    // Below this the title block goes, so the drop card and a few rows of each list still fit.
    private const double HeroMinHeight = 650;

    public EmptyStateView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Hero.IsVisible = e.NewSize.Height >= HeroMinHeight;
        LocationsButton.Flyout = locationsMenu;
        locationsMenu.Opening += (_, _) => LocationsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Binds the recent-files list and toggles the empty-state text / clear button.</summary>
    public void SetRecentFiles(IReadOnlyList<RecentFileItem> items)
    {
        RecentFilesList.ItemsSource = items;
        NoRecentFilesText.IsVisible = items.Count == 0;
        ClearRecentFilesButton.IsVisible = items.Count > 0;
    }

    /// <summary>Binds the favourites list, showing a hint in its place while nothing is pinned.</summary>
    public void SetFavourites(IReadOnlyList<RecentFileItem> items)
    {
        FavouritesList.ItemsSource = items;
        NoFavouritesText.IsVisible = items.Count == 0;
    }

    /// <summary>Fills the locations menu, and hides its button when there is nothing to offer.</summary>
    public void SetLocations(IReadOnlyList<ConfigLocation> locations)
    {
        locationsMenu.Items.Clear();
        foreach (var location in locations)
        {
            var item = new MenuItem { Header = location.Name };
            ToolTip.SetTip(item, location.Path);
            item.Click += (_, _) => BrowseFromFolderRequested?.Invoke(this, location.Path);
            locationsMenu.Items.Add(item);
        }

        LocationsButton.IsVisible = locations.Count > 0;
    }

    /// <summary>Hides the paste affordance when the platform gave us no clipboard to read.</summary>
    public void SetPasteAvailable(bool available) => PasteButton.IsVisible = available;

    private void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        ChooseFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPaste(object? sender, RoutedEventArgs e)
    {
        PasteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearRecentFiles(object? sender, RoutedEventArgs e)
    {
        ClearRecentFilesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleFavourite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is RecentFileItem item)
            ToggleFavouriteRequested?.Invoke(this, item.Path);
    }

    private void OnRecentFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is RecentFileItem item)
            OpenRecentFileRequested?.Invoke(this, item.Path);
    }
}
