using System;
using System.Collections.Generic;
using System.Linq;
using Argonaut.Engine.Locations;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

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
    public event EventHandler? NewRequestRequested;
    public event EventHandler? ClearRecentRequested;
    public event EventHandler? ClearKeptResponsesRequested;

    /// <summary>Raised with the id of the saved source to open.</summary>
    public event EventHandler<string>? OpenSavedSourceRequested;

    /// <summary>Raised with the id of the saved source to pin or unpin.</summary>
    public event EventHandler<string>? TogglePinRequested;

    /// <summary>Raised with the id of a saved source and the name the user gave it.</summary>
    public event EventHandler<(string Id, string Name)>? RenameRequested;

    /// <summary>Raised with the id of the saved source to take off the list.</summary>
    public event EventHandler<string>? RemoveRequested;

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

    /// <summary>Offers "Clear kept responses" while there are some to clear.</summary>
    public void SetHasKeptResponses(bool any) => ClearKeptResponsesButton.IsVisible = any;

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

    private void OnNewRequest(object? sender, RoutedEventArgs e)
    {
        NewRequestRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearKeptResponses(object? sender, RoutedEventArgs e)
    {
        ClearKeptResponsesRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Lays the rename box over the row's name, filled and selected.</summary>
    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not SavedSourceRow row || RenameBoxOf(row) is not { } box)
            return;

        box.Text = row.Name;
        box.IsVisible = true;
        box.Focus();
        box.SelectAll();
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is SavedSourceRow row)
            RemoveRequested?.Invoke(this, row.Id);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SavedSourceRow row)
            return;

        if (e.Key == Key.Enter)
        {
            box.IsVisible = false;
            RenameRequested?.Invoke(this, (row.Id, box.Text ?? string.Empty));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.IsVisible = false;
            e.Handled = true;
        }
    }

    // Clicking away drops the edit, as Escape does: only Enter changes the name.
    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            box.IsVisible = false;
    }

    /// <summary>The rename box in <paramref name="row"/>'s container, in whichever list shows it.
    /// Found from the list rather than from the menu item, which lives in a popup outside the row.</summary>
    private TextBox? RenameBoxOf(SavedSourceRow row)
    {
        foreach (var list in new[] { FavouritesList, RecentFilesList })
        {
            if (list.ContainerFromItem(row) is { } container)
                return container.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault();
        }

        return null;
    }

    private void OnRecentFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SavedSourceRow row)
            OpenSavedSourceRequested?.Invoke(this, row.Id);
    }
}
