using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Argonaut.Infrastructure;
using System.Threading.Tasks;

namespace Argonaut.Shell;

/// <summary>
/// Thin view over <see cref="MainWindowViewModel"/>: window-level input (find shortcuts,
/// drag-and-drop), the file picker and replace-confirmation dialog (both need the window),
/// the toast overlay, and the Avalonia-specific reactions to the view model's theme mode
/// (theme variant, toggle icon, tooltip). All file-open/close and status logic lives in the
/// view model.
/// </summary>
public partial class MainWindow : Window
{
    // Material "desktop_windows" / "wb_sunny" / "brightness_2" glyphs (24x24 viewBox),
    // cycled by the status bar's theme toggle button.
    private const string SystemThemeIconData =
        "M21 2H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h7l-2 3v1h8v-1l-2-3h7c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H3V4h18v12z";
    private const string LightThemeIconData =
        "M6.76 4.84l-1.8-1.79-1.41 1.41 1.79 1.79 1.42-1.41zM4 10.5H1v2h3v-2zm9-9.95h-2V3.5h2V.55zm7.45 3.91l-1.41-1.41-1.79 1.79 1.41 1.41 1.79-1.79zm-3.21 13.7l1.79 1.8 1.41-1.41-1.8-1.79-1.4 1.4zM20 10.5v2h3v-2h-3zm-8-5c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6-2.69-6-6-6zm-1 16.95h2V19.5h-2v2.95zm-7.45-3.91l1.41 1.41 1.79-1.8-1.41-1.41-1.79 1.8z";
    private const string DarkThemeIconData =
        "M12 3a9 9 0 1 0 9 9c0-.46-.04-.92-.1-1.36a5.389 5.389 0 0 1-4.4 2.26 5.403 5.403 0 0 1-3.14-9.8c-.44-.06-.9-.1-1.36-.1z";

    private readonly MainWindowViewModel viewModel;
    private DispatcherTimer? toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        viewModel = new MainWindowViewModel(
            message => ConfirmDialog.Show(this, message));
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FindStatusChanged += status => FindBarControl.SetStatus(status);
        viewModel.FindBarResetRequested += () => FindBarControl.Reset();

        ToastService.Requested += ShowToast;

        EmptyState.ChooseFileRequested += async (_, _) => await BrowseForFile();
        EmptyState.OpenRecentFileRequested += (_, path) => viewModel.OpenRecentFile(path);
        EmptyState.ClearRecentFilesRequested += (_, _) => viewModel.ClearRecentFiles();
        EmptyState.SetRecentFiles(viewModel.RecentFiles);

        ApplyThemeMode(viewModel.ThemeMode);

        FindBarControl.FindRequested += (term, direction) => _ = viewModel.FindAsync(term, direction);
        FindBarControl.ResetRequested += CloseFindBar;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainWindowViewModel.ThemeMode))
            ApplyThemeMode(viewModel.ThemeMode);

        if (e.PropertyName is null or nameof(MainWindowViewModel.RecentFiles))
            EmptyState.SetRecentFiles(viewModel.RecentFiles);
    }

    public async Task OpenInitialFileAsync(string path)
    {
        OpenDebugLog.Write($"OpenInitialFileAsync: {path}");
        try
        {
            await viewModel.OpenPathAsync(path);
            OpenDebugLog.Write($"OpenInitialFileAsync completed, currentFilePath={viewModel.FilePath ?? "<null>"}");
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenInitialFileAsync threw: {ex}");
        }
    }

    /// <summary>
    /// Window-wide find shortcuts. Tunneling so they fire regardless of which control has
    /// focus (including the find bar's own TextBox, which never sees a handled event).
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        bool cmdOrCtrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        if (e.Key == Key.F && cmdOrCtrl)
        {
            if (viewModel.IsFileOpen)
            {
                FindBarControl.FocusTerm();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape && viewModel.IsFileOpen)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F3 || (e.Key == Key.G && cmdOrCtrl)) && viewModel.IsFileOpen)
        {
            FindBarControl.RequestFind((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
    }

    private void CloseFindBar()
    {
        _ = viewModel.StopFindAsync();
        FindBarControl.Reset();
        ContentArea.Focus();
    }

    private void OnToggleTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        viewModel.ToggleTheme();
    }

    private void ApplyThemeMode(ThemeMode mode)
    {
        Application.Current!.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        ThemeToggleIcon.Data = Geometry.Parse(mode switch
        {
            ThemeMode.Light => LightThemeIconData,
            ThemeMode.Dark => DarkThemeIconData,
            _ => SystemThemeIconData
        });

        ToolTip.SetTip(ThemeToggleButton, mode switch
        {
            ThemeMode.Light => "Theme: Light (click for Dark)",
            ThemeMode.Dark => "Theme: Dark (click to follow System)",
            _ => "Theme: System (click for Light)"
        });
    }

    private async void OnCloseFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await viewModel.CloseFileAsync();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Items.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var path = GetDroppedFilePath(e);
        if (path is null)
            return;

        await viewModel.OpenPathAsync(path);
    }

    private static string? GetDroppedFilePath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        var file = files?.FirstOrDefault();
        return file?.TryGetLocalPath();
    }

    private async Task BrowseForFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open JSON, NDJSON, CSV, or TSV file",
            AllowMultiple = false
        });

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;

        await viewModel.OpenPathAsync(path);
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.IsVisible = true;

        toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        toastTimer.Stop();
        toastTimer.Tick -= OnToastTimerTick;
        toastTimer.Tick += OnToastTimerTick;
        toastTimer.Start();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        toastTimer!.Stop();
        ToastBorder.IsVisible = false;
    }
}
