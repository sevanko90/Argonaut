using System;
using System.Collections.Generic;
using Argonaut.Engine.Locations;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Argonaut.Shell;

/// <summary>One row in the recent or pinned list: the saved source's id, the name shown on the
/// button, where it is (the tooltip), the kind pill for anything but a file, and whether it is
/// pinned (which fills its star).</summary>
public sealed record SavedSourceRow(string Id, string Name, string LocationText, string? Pill, bool IsPinned)
{
    public bool HasPill => Pill is not null;
}

public partial class EmptyStateView : UserControl
{
    public event EventHandler? ChooseFileRequested;
    public event EventHandler? PasteRequested;
    public event EventHandler? NewDocumentRequested;
    public event EventHandler? ClearRecentRequested;

    /// <summary>Raised with the id of the saved source to open.</summary>
    public event EventHandler<string>? OpenSavedSourceRequested;

    /// <summary>Raised with the id of the saved source to pin or unpin.</summary>
    public event EventHandler<string>? TogglePinRequested;

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

    /// <summary>Binds the recent list and toggles the empty-state text / clear button.</summary>
    public void SetRecent(IReadOnlyList<SavedSourceRow> items)
    {
        RecentFilesList.ItemsSource = items;
        NoRecentFilesText.IsVisible = items.Count == 0;
        ClearRecentFilesButton.IsVisible = items.Count > 0;
    }

    /// <summary>Binds the pinned list, showing a hint in its place while nothing is pinned.</summary>
    public void SetPinned(IReadOnlyList<SavedSourceRow> items)
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

    private void OnNewDocument(object? sender, RoutedEventArgs e)
    {
        NewDocumentRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearRecentFiles(object? sender, RoutedEventArgs e)
    {
        ClearRecentRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleFavourite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SavedSourceRow row)
            TogglePinRequested?.Invoke(this, row.Id);
    }

    private void OnRecentFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SavedSourceRow row)
            OpenSavedSourceRequested?.Invoke(this, row.Id);
    }
}
