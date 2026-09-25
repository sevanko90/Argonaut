using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Settings;
using Argonaut.Features.Json.Schema;
using Argonaut.Shell.Dialogs;
using Argonaut.Shell.Updates;
using Argonaut.Ui.Documents.Navigation;
using Argonaut.Ui.Notifications;
using Argonaut.Ui.Progress;
using System.Threading.Tasks;
using Velopack;

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
    private readonly UpdateService updateService;
    private readonly ISettingsStore settings;
    private readonly UpdateSettings updateSettings;
    private DispatcherTimer? toastTimer;

    /// <summary>For the XAML runtime loader and the designer only - settings go nowhere. The app
    /// constructs the window through the other constructor.</summary>
    public MainWindow() : this(SettingsStore.InMemory(), new JsonSchemaCatalog(
        JsonSchemaCatalog.BundledDirectoryBesideApp, AppDataPaths.SchemasDirectory, revealDirectory: _ => { }))
    {
    }

    public MainWindow(ISettingsStore settings, JsonSchemaCatalog schemaCatalog)
    {
        InitializeComponent();

        this.settings = settings;
        updateSettings = settings.Get<UpdateSettings>();
        updateService = new UpdateService(updateSettings);
        viewModel = new MainWindowViewModel(settings, schemaCatalog,
            message => ConfirmDialog.Show(this, message),
            readClipboardBytes: ReadClipboardBytesAsync,
            pickSaveDestination: PickSaveDestinationAsync,
            askAboutUnsavedChanges: message => UnsavedChangesDialog.Show(this, message),
            reportFailure: message => ConfirmDialog.Inform(this, message));
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FindStatusChanged += status => FindBarControl.SetStatus(status);
        viewModel.FindBarResetRequested += () => FindBarControl.Reset();

        ToastService.Requested += ShowToast;
        viewModel.Progress.WorkStarted += (_, _) => StartProgressTicks();
        RawJumpService.Requested += range => _ = viewModel.RevealInTextViewAsync(range);
        ArrayTableService.Requested += request => _ = viewModel.OpenArrayTableAsync(request);

        // The platform's own modifier, so the menu shows the shortcut the key handler honours.
        // Reached through the button rather than by name: a control named inside a flyout is not
        // reliably in the window's name scope, and would be null here.
        if (SaveOptionsButton.Flyout is MenuFlyout { Items: [MenuItem saveAs, ..] })
        {
            saveAs.InputGesture = new KeyGesture(Key.S,
                (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Shift);
        }

        EmptyState.ChooseFileRequested += async (_, _) => await BrowseForFile();
        EmptyState.PasteRequested += async (_, _) => await viewModel.PasteAsync();
        EmptyState.SetPasteAvailable(viewModel.CanPaste);
        EmptyState.OpenRecentFileRequested += (_, path) => viewModel.OpenRecentFile(path);
        EmptyState.ClearRecentFilesRequested += (_, _) => viewModel.ClearRecentFiles();
        EmptyState.SetRecentFiles(viewModel.RecentFiles);

        ApplyThemeMode(viewModel.ThemeMode);
        ApplyContentFontMode(viewModel.ContentFontMode);

        FindBarControl.FindRequested += (term, direction) => _ = viewModel.FindAsync(term, direction);
        FindBarControl.ResetRequested += CloseFindBar;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _ = CheckForUpdatesOnStartupAsync();
    }

    /// <summary>
    /// Platform formats that hand text over as UTF-8 bytes, in preference order. Taking one of
    /// these skips a whole representation: Avalonia's TryGetTextAsync yields a
    /// <see cref="string"/>, so a paste would arrive as UTF-16 (two bytes per ASCII character,
    /// on the large object heap at any size worth worrying about) and then be transcoded to the
    /// UTF-8 the indexers read. Asking for the bytes directly does neither.
    ///
    /// macOS requires a Uniform Type Identifier; X11 and Wayland use mime types. Windows has no
    /// standard UTF-8 clipboard format, so it falls through to the string path - which is why
    /// this is a preference rather than a requirement.
    /// </summary>
    private static readonly string[] Utf8ClipboardFormats =
    {
        "public.utf8-plain-text",        // macOS
        "text/plain;charset=utf-8",      // X11 / Wayland
    };

    /// <summary>
    /// The clipboard's text as UTF-8 bytes, or null when there is none (or no clipboard at all).
    /// Passed to the view model as a delegate so it stays free of Avalonia's TopLevel, and so
    /// tests can supply clipboard contents without one.
    ///
    /// There is no way to ask how large the contents are first, and no way to read them
    /// incrementally: every path here returns the whole payload in one allocation. That is a
    /// limit of the clipboard APIs rather than of this method - the platform ones that could do
    /// better (Windows can report an HGLOBAL's size before copying it; X11's INCR protocol and
    /// Wayland's file descriptor are genuinely incremental) disagree with each other enough that
    /// three native backends would be needed to exploit it, for a case the size cap already
    /// bounds. See MainWindowViewModel.PasteAsync.
    /// </summary>
    private async Task<byte[]?> ReadClipboardBytesAsync()
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return null;

        var transfer = await clipboard.TryGetDataAsync();
        if (transfer is null)
            return null;

        try
        {
            // Formats can be inspected without fetching anything, so preferring a UTF-8 format
            // costs nothing when the clipboard does not offer one.
            foreach (var identifier in Utf8ClipboardFormats)
            {
                var format = DataFormat.CreateBytesPlatformFormat(identifier);
                if (!transfer.Contains(format))
                    continue;

                if (await transfer.TryGetValueAsync(format) is { Length: > 0 } utf8)
                    return utf8;
            }

            string? text = await transfer.TryGetTextAsync();
            return text is null ? null : Encoding.UTF8.GetBytes(text);
        }
        finally
        {
            (transfer as IDisposable)?.Dispose();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainWindowViewModel.ThemeMode))
            ApplyThemeMode(viewModel.ThemeMode);

        if (e.PropertyName is null or nameof(MainWindowViewModel.ContentFontMode))
            ApplyContentFontMode(viewModel.ContentFontMode);

        if (e.PropertyName is null or nameof(MainWindowViewModel.RecentFiles))
            EmptyState.SetRecentFiles(viewModel.RecentFiles);

#if DEBUG
        if (e.PropertyName is null or nameof(MainWindowViewModel.CurrentDocument))
            DetachInternalsInspector();
#endif
    }

    /// <param name="second">A second command-line path (e.g. `argonaut a.json b.json`), or
    /// null for the single-file case. See <see cref="MainWindowViewModel.OpenPathsAsync"/> for
    /// how the pair decides between diff mode and opening <paramref name="first"/> alone.</param>
    public async Task OpenInitialFileAsync(string? first, string? second = null)
    {
        OpenDebugLog.Write($"OpenInitialFileAsync: first={first}, second={second}");
        try
        {
            await viewModel.OpenPathsAsync(first, second);
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

        if (e.Key == Key.S && cmdOrCtrl)
        {
            if (viewModel.IsSaveAvailable)
            {
                _ = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? viewModel.SaveAsAsync() : viewModel.SaveAsync();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.T && cmdOrCtrl)
        {
            if (viewModel.CanToggleTextView)
            {
                _ = viewModel.ToggleTextViewAsync();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.F && cmdOrCtrl)
        {
            if (viewModel.IsFileOpen)
            {
                FindBarControl.FocusTerm();
                e.Handled = true;
            }

            return;
        }

        // Only while nothing is open. Once the raw view's editing lands, plain Ctrl+V inside it
        // has to mean "paste into the document", not "replace the document" - so this shortcut
        // deliberately does not exist when there is a document to paste into. The empty state's
        // button is the affordance that always works.
        if (e.Key == Key.V && cmdOrCtrl && !viewModel.IsFileOpen && viewModel.CanPaste)
        {
            _ = viewModel.PasteAsync();
            e.Handled = true;
            return;
        }

#if DEBUG
        // Development only - the whole Diagnostics folder is excluded from the build outside
        // Debug (see Argonaut.csproj), so this shortcut cannot exist in a shipped binary.
        if (e.Key == Key.D && cmdOrCtrl && (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            ShowInternalsInspector();
            e.Handled = true;
            return;
        }
#endif

        if (e.Key == Key.Escape && viewModel.IsFileOpen)
        {
            // Escape is the way out of the raw editor's edit mode, and this handler tunnels -
            // it sees the key before the surface does. Dismissing the find bar also pulls focus
            // back to the content area, so handling it here while the user is typing would end
            // the edit session's focus as well as its mode.
            if (viewModel.CurrentDocument is Features.Raw.RawViewModel { IsEditing: true } editing)
            {
                editing.SetEditing(false);
                e.Handled = true;
                return;
            }

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

#if DEBUG
    private Diagnostics.RawEditInspectorWindow? internalsInspector;

    /// <summary>
    /// Opens the raw editor's internals inspector, or brings the open one forward. Re-opened
    /// rather than re-targeted when the document has changed: a snapshot copies what it shows,
    /// so the old window is still readable, but it belongs to a document that is gone.
    /// </summary>
    private void ShowInternalsInspector()
    {
        if (viewModel.CurrentDocument is not Features.Raw.RawViewModel raw)
        {
            ToastService.Show("Internals inspector: open a file in the raw viewer first.");
            return;
        }

        if (internalsInspector is { } open && open.IsVisible)
        {
            open.Activate();
            return;
        }

        internalsInspector = new Diagnostics.RawEditInspectorWindow(raw);
        internalsInspector.Closed += (_, _) => internalsInspector = null;
        internalsInspector.Show(this);
    }

    /// <summary>The inspector follows one document; when that document goes, it stops following
    /// rather than reading a view model that is being torn down.</summary>
    private void DetachInternalsInspector() => internalsInspector?.Detach();
#endif

    private void CloseFindBar()
    {
        viewModel.StopFind();
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

    private void OnToggleContentFont(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        viewModel.ToggleContentFont();
    }

    /// <summary>
    /// Repoints the AppContentFontFamily application resource at the mono or sans family.
    /// Content views consume it via DynamicResource, so replacing the entry re-fonts them
    /// all live — the same mechanism the theme brushes use.
    /// </summary>
    private void ApplyContentFontMode(ContentFontMode mode)
    {
        var app = Application.Current!;
        string sourceKey = mode == ContentFontMode.SansSerif ? "AppSansFontFamily" : "AppMonoFontFamily";
        app.Resources["AppContentFontFamily"] = app.Resources[sourceKey];

        ToolTip.SetTip(FontToggleButton, mode == ContentFontMode.SansSerif
            ? "Content font: Sans-serif (click for Monospace)"
            : "Content font: Monospace (click for Sans-serif)");
    }

    private async void OnCloseFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await viewModel.CloseFileAsync();
    }

    private async void OnSaveFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await viewModel.SaveAsync();
    }

    private async void OnSaveFileAs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await viewModel.SaveAsAsync();
    }

    /// <summary>
    /// Asks where to save <paramref name="current"/>, starting beside its file and under its name
    /// when it has one. The picker asks about overwriting an existing file itself.
    /// </summary>
    private async Task<string?> PickSaveDestinationAsync(IByteOrigin current)
    {
        var options = new FilePickerSaveOptions
        {
            Title = "Save as",
            SuggestedFileName = current.Path is { } path ? System.IO.Path.GetFileName(path) : $"{current.DisplayName}.txt",
            ShowOverwritePrompt = true,
        };

        if (current.Path is { } file && System.IO.Path.GetDirectoryName(file) is { } folder)
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder);

        var picked = await StorageProvider.SaveFilePickerAsync(options);
        return picked?.TryGetLocalPath();
    }

    // Set once the user has dealt with unsaved changes, so the Close that follows goes through.
    private bool closeAgreed;

    /// <summary>
    /// Closing the window would drop unsaved edits, so it is held while the user is asked -
    /// Avalonia's close cannot wait on a dialog, so this cancels it and closes again once the
    /// answer allows. A save still running holds the close outright: it is part way through
    /// swapping the user's file.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || this.closeAgreed)
            return;

        if (viewModel.IsSaving)
        {
            e.Cancel = true;
            ToastService.Show("Wait for the save to finish.");
            return;
        }

        if (!viewModel.HasUnsavedChanges)
            return;

        e.Cancel = true;
        _ = CloseAfterResolvingUnsavedChangesAsync();
    }

    private async Task CloseAfterResolvingUnsavedChangesAsync()
    {
        if (!await viewModel.ResolveUnsavedChangesAsync("quitting"))
            return;

        this.closeAgreed = true;
        Close();
    }

    private async void OnShowAbout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await AboutDialog.ShowAbout(this, updateSettings);
    }

    /// <summary>
    /// Silent, throttled startup check: no toast/dialog when there's nothing new, so a normal
    /// launch is undisturbed. Errors (offline, rate-limited, etc.) are swallowed here since
    /// there's no user action driving this check to report failure against.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!updateService.IsInstalled || !updateService.ShouldCheckOnStartup())
            return;

        updateService.RecordStartupCheck();

        UpdateInfo? info;
        try
        {
            info = await updateService.CheckForUpdatesAsync();
        }
        catch
        {
            return;
        }

        if (info is not null)
            await OfferUpdateAsync(info);
    }

    private async void OnCheckForUpdates(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!updateService.IsInstalled)
        {
            ToastService.Show("Auto-update isn't available for this build");
            return;
        }

        UpdateInfo? info;
        try
        {
            info = await updateService.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            ToastService.Show($"Update check failed: {ex.Message}");
            return;
        }

        if (info is null)
        {
            ToastService.Show("You're up to date");
            return;
        }

        await OfferUpdateAsync(info);
    }

    /// <summary>
    /// Shared confirm-download-restart flow, driven either by the silent startup check or the
    /// manual toolbar button. Declining either prompt just leaves the update for next time
    /// (re-offered on the next check) rather than tracking a separate "staged" state.
    /// </summary>
    private async Task OfferUpdateAsync(UpdateInfo info)
    {
        string version = info.TargetFullRelease.Version.ToString();

        bool download = await ConfirmDialog.Show(
            this, $"Update available (v{version}). Download and install now?", "Download");
        if (!download)
            return;

        try
        {
            await updateService.DownloadUpdatesAsync(
                info, progress => ToastService.Show($"Downloading update... {progress}%"));
        }
        catch (Exception ex)
        {
            ToastService.Show($"Update download failed: {ex.Message}");
            return;
        }

        bool restart = await ConfirmDialog.Show(
            this, $"Update downloaded (v{version}). Restart Argonaut now to apply it?", "Restart");
        if (restart && await viewModel.ResolveUnsavedChangesAsync("restarting"))
            // Velopack ends the process without the app's Exit, which is where settings are saved.
            settings.Save();
            updateService.ApplyUpdatesAndRestart(info);
    }

    private void OnToggleTextView(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = viewModel.ToggleTextViewAsync();
    }

    private void OnJumpToFailureLine(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = viewModel.JumpToRawOffsetAsync(viewModel.CurrentDocument?.IndexFailure?.ByteOffset ?? 0);
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

    private async void OnCompareFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the JSON file to compare with",
            AllowMultiple = false
        });

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (path is null)
            return;

        await viewModel.CompareWithAsync(path);
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

    private DispatcherTimer? progressTimer;

    /// <summary>
    /// Drives the progress board's show/hide rules while it has anything to decide - pending,
    /// shown or fading - and stops once it has not, so an idle app runs no timer.
    /// </summary>
    private void StartProgressTicks()
    {
        progressTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnProgressTick);
        progressTimer.Start();
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        var board = viewModel.Progress;
        board.Tick();
        if (!board.HasWork)
            progressTimer!.Stop();
    }

    private void OnStopProgress(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ProgressEntry entry)
            entry.RequestStop();
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
