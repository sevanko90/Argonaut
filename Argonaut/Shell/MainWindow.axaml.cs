using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.NdJson;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;
using System.Threading.Tasks;

namespace Argonaut.Shell;

public partial class MainWindow : Window
{
    private const string DefaultTitle = "Argonaut";

    // Material "desktop_windows" / "wb_sunny" / "brightness_2" glyphs (24x24 viewBox),
    // cycled by the status bar's theme toggle button.
    private const string SystemThemeIconData =
        "M21 2H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h7l-2 3v1h8v-1l-2-3h7c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H3V4h18v12z";
    private const string LightThemeIconData =
        "M6.76 4.84l-1.8-1.79-1.41 1.41 1.79 1.79 1.42-1.41zM4 10.5H1v2h3v-2zm9-9.95h-2V3.5h2V.55zm7.45 3.91l-1.41-1.41-1.79 1.79 1.41 1.41 1.79-1.79zm-3.21 13.7l1.79 1.8 1.41-1.41-1.8-1.79-1.4 1.4zM20 10.5v2h3v-2h-3zm-8-5c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6-2.69-6-6-6zm-1 16.95h2V19.5h-2v2.95zm-7.45-3.91l1.41 1.41 1.79-1.8-1.41-1.41-1.79 1.8z";
    private const string DarkThemeIconData =
        "M12 3a9 9 0 1 0 9 9c0-.46-.04-.92-.1-1.36a5.389 5.389 0 0 1-4.4 2.26 5.403 5.403 0 0 1-3.14-9.8c-.44-.06-.9-.1-1.36-.1z";

    private readonly EmptyStateView emptyStateView = new();
    private readonly FindController findController;
    private NdJsonViewModel? currentNdJsonViewModel;
    private int openRequestId;
    private string? currentFilePath;
    private ThemeMode currentThemeMode;
    private DateHintSettings? currentHintSettings;
    private bool suppressHintComboEvents;
    private int currentExpandDepth;
    private bool suppressExpandDepthComboEvent;
    private DispatcherTimer? toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        Title = DefaultTitle;
        ToastService.Requested += ShowToast;
        emptyStateView.ChooseFileRequested += async (_, _) => await BrowseForFile();
        emptyStateView.OpenRecentFileRequested += (_, path) => _ = OpenPath(path);
        emptyStateView.ClearRecentFilesRequested += (_, _) =>
        {
            RecentFileHistory.Clear();
            ReloadRecentFiles();
        };
        ContentArea.Content = emptyStateView;
        ReloadRecentFiles();
        ApplyThemeMode(ThemePreference.Load());

        currentExpandDepth = ExpandDepthPreference.Load();
        SyncExpandDepthCombo();

        findController = new FindController(
            status => FindBarControl.SetStatus(status),
            () => currentFilePath is null ? null : new StatusProgressReporter(this, currentFilePath, openRequestId));
        FindBarControl.FindRequested += (term, direction) => _ = findController.FindAsync(term, direction);
        FindBarControl.ResetRequested += CloseFindBar;

        DateHintSchemeCombo.SelectionChanged += OnSchemeComboChanged;
        DateHintTimeZoneCombo.SelectionChanged += OnTimeZoneComboChanged;
        ExpandDepthCombo.SelectionChanged += OnExpandDepthComboChanged;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
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
            if (currentFilePath is not null)
            {
                FindBarControl.FocusTerm();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape && currentFilePath is not null)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F3 || (e.Key == Key.G && cmdOrCtrl)) && currentFilePath is not null)
        {
            FindBarControl.RequestFind((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
    }

    private void CloseFindBar()
    {
        _ = findController.StopAsync();
        FindBarControl.Reset();
        ContentArea.Focus();
    }

    private void OnToggleTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var next = currentThemeMode switch
        {
            ThemeMode.System => ThemeMode.Light,
            ThemeMode.Light => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        ApplyThemeMode(next);
        ThemePreference.Save(next);
    }

    private void ApplyThemeMode(ThemeMode mode)
    {
        currentThemeMode = mode;

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
        await CloseFile();
    }

    private async Task CloseFile()
    {
        ++openRequestId;

        // The search scan holds spans over the current view's MMapFile - it must be fully
        // stopped before the content swap below detaches (and thereby disposes) that view.
        await findController.DetachAsync();
        FindBarControl.Reset();

        DetachNdJsonViewModel();
        DetachHintSettings();
        currentFilePath = null;
        ContentArea.Content = emptyStateView;
        ToolbarBar.IsVisible = false;
        StatusText.Text = "No file loaded";
        Title = DefaultTitle;
        ReloadRecentFiles();
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

        await OpenPath(path);
    }

    private static string? GetDroppedFilePath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        var file = files?.FirstOrDefault();
        return file?.TryGetLocalPath();
    }

    public async Task OpenInitialFileAsync(string path)
    {
        OpenDebugLog.Write($"OpenInitialFileAsync: {path}");
        try
        {
            await OpenPath(path);
            OpenDebugLog.Write($"OpenInitialFileAsync completed, currentFilePath={currentFilePath ?? "<null>"}");
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenInitialFileAsync threw: {ex}");
        }
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

        await OpenPath(path);
    }

    private async Task OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            OpenDebugLog.Write("OpenPath: path is null/whitespace, returning");
            return;
        }

        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath))
        {
            OpenDebugLog.Write($"OpenPath: File.Exists false for '{normalizedPath}'");
            return;
        }

        if (currentFilePath is not null && !string.Equals(currentFilePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            var confirmed = await ConfirmDialog.Show(
                this,
                $"Replace the currently loaded file with \"{Path.GetFileName(normalizedPath)}\"?");
            if (!confirmed)
                return;
        }

        var requestId = ++openRequestId;

        FileTypeDetector.FileKind fileType;
        try
        {
            fileType = FileTypeDetector.DetectFileType(normalizedPath);
        }
        catch (Exception ex)
        {
            OpenDebugLog.Write($"OpenPath: DetectFileType threw: {ex}");
            return;
        }

        OpenDebugLog.Write($"OpenPath: normalizedPath='{normalizedPath}', fileType={fileType}");

        switch (fileType)
        {
            case FileTypeDetector.FileKind.Unidentified:
                // can't do anything with it
                break;
            case FileTypeDetector.FileKind.Json:
            {
                // Stop any search over the outgoing file before its view (and MMapFile) is
                // replaced/disposed by the content swap below.
                await findController.DetachAsync();
                FindBarControl.Reset();
                DetachNdJsonViewModel();
                DetachHintSettings();
                StatusText.Text = $"Indexing {normalizedPath}… 0%";

                var reporter = new StatusProgressReporter(this, normalizedPath, requestId);
                var vm = new JsonViewModel { DefaultExpandDepth = currentExpandDepth };
                await vm.LoadAsync(normalizedPath, reporter);
                if (requestId != openRequestId)
                {
                    vm.Dispose();
                    return;
                }

                ContentArea.Content = new JsonView { DataContext = vm };
                findController.Attach(new JsonSearchNavigator(vm));
                currentFilePath = normalizedPath;
                ShowToolbar(normalizedPath);
                AttachHintSettings(vm.HintSettings);
                RecentFileHistory.Add(normalizedPath);
                ReloadRecentFiles();
                StatusText.Text = $"{normalizedPath} — {vm.TokenCount:N0} tokens indexed so far";
                _ = MonitorIndexingCompletionAsync(vm.Index!, normalizedPath, requestId);
                break;
            }
            case FileTypeDetector.FileKind.Ndjson:
            {
                await findController.DetachAsync();
                FindBarControl.Reset();
                DetachNdJsonViewModel();
                DetachHintSettings();
                StatusText.Text = $"Indexing {normalizedPath}… 0%";

                var reporter = new StatusProgressReporter(this, normalizedPath, requestId);
                var vm = new NdJsonViewModel { DefaultExpandDepth = currentExpandDepth };
                await vm.LoadAsync(normalizedPath, reporter);
                if (requestId != openRequestId)
                {
                    vm.Dispose();
                    return;
                }

                currentNdJsonViewModel = vm;
                currentNdJsonViewModel.PropertyChanged += OnNdJsonViewModelPropertyChanged;
                ContentArea.Content = new NdJsonView { DataContext = vm };
                findController.Attach(new NdJsonSearchNavigator(vm));
                currentFilePath = normalizedPath;
                ShowToolbar(normalizedPath);
                AttachHintSettings(vm.HintSettings);
                RecentFileHistory.Add(normalizedPath);
                ReloadRecentFiles();
                UpdateNdJsonStatus(vm);
                // Keeps the "Selected line" suffix if a line is selected when indexing finishes.
                _ = MonitorIndexingCompletionAsync(vm.Index!, normalizedPath, requestId, () => UpdateNdJsonStatus(vm));
                break;
            }
            case FileTypeDetector.FileKind.Csv:
                await OpenCsvAsync(normalizedPath, (byte)',', requestId);
                break;
            case FileTypeDetector.FileKind.Tsv:
                await OpenCsvAsync(normalizedPath, (byte)'\t', requestId);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>
    /// Shared by the Csv/Tsv switch cases above - identical apart from the delimiter byte. No
    /// DateHintSettings attach - CSV/TSV has no date-hint concept.
    /// </summary>
    private async Task OpenCsvAsync(string path, byte delimiter, int requestId)
    {
        await findController.DetachAsync();
        FindBarControl.Reset();
        DetachNdJsonViewModel();
        DetachHintSettings();
        StatusText.Text = $"Indexing {path}… 0%";

        var reporter = new StatusProgressReporter(this, path, requestId);
        var vm = new CsvViewModel();
        await vm.LoadAsync(path, delimiter, reporter);
        if (requestId != openRequestId)
        {
            vm.Dispose();
            return;
        }

        ContentArea.Content = new CsvView { DataContext = vm };
        findController.Attach(new CsvSearchNavigator(vm));
        currentFilePath = path;
        ShowToolbar(path);
        RecentFileHistory.Add(path);
        ReloadRecentFiles();
        StatusText.Text = $"{path} — {vm.RowCount:N0} rows indexed so far";
        _ = MonitorIndexingCompletionAsync(vm.Index!, path, requestId, () => StatusText.Text = $"{path} — {vm.RowCount:N0} rows");
    }

    private void OnNdJsonViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NdJsonViewModel vm)
            return;

        if (e.PropertyName is null or nameof(NdJsonViewModel.SelectedLineNumber))
            UpdateNdJsonStatus(vm);
    }

    private void UpdateNdJsonStatus(NdJsonViewModel vm)
    {
        if (vm.SelectedLineNumber is null)
        {
            StatusText.Text = $"{vm.FilePath} — {vm.LineCount:N0} lines";
            return;
        }

        StatusText.Text = $"{vm.FilePath} — {vm.LineCount:N0} lines — Selected line: {vm.SelectedLineNumber:N0}";
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

    private void ShowToolbar(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        FileNameText.Text = fileName;
        ToolTip.SetTip(FileNameText, filePath);
        ToolbarBar.IsVisible = true;
        Title = $"{DefaultTitle} — {fileName}";
    }

    private void DetachNdJsonViewModel()
    {
        if (currentNdJsonViewModel is null)
            return;

        currentNdJsonViewModel.PropertyChanged -= OnNdJsonViewModelPropertyChanged;
        currentNdJsonViewModel = null;
    }

    private void AttachHintSettings(DateHintSettings settings)
    {
        DetachHintSettings();
        currentHintSettings = settings;
        settings.PropertyChanged += OnHintSettingsPropertyChanged;
        SyncSchemeCombo();
        SyncTimeZoneCombo();
    }

    private void DetachHintSettings()
    {
        if (currentHintSettings is not null)
            currentHintSettings.PropertyChanged -= OnHintSettingsPropertyChanged;

        currentHintSettings = null;
        SyncSchemeCombo();
        SyncTimeZoneCombo();
    }

    private void OnHintSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Inference completing in the background updates FileDefaultScheme - reflect it live.
        if (e.PropertyName is null or nameof(DateHintSettings.FileDefaultScheme))
            SyncSchemeCombo();

        if (e.PropertyName is null or nameof(DateHintSettings.TimeZoneMode))
            SyncTimeZoneCombo();
    }

    private void SyncSchemeCombo()
    {
        suppressHintComboEvents = true;
        try
        {
            DateHintSchemeCombo.SelectedIndex = (int)(currentHintSettings?.FileDefaultScheme ?? DateDecodingScheme.Off);
        }
        finally
        {
            suppressHintComboEvents = false;
        }
    }

    private void OnSchemeComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressHintComboEvents || currentHintSettings is null || DateHintSchemeCombo.SelectedIndex < 0)
            return;

        currentHintSettings.SetUserDefault((DateDecodingScheme)DateHintSchemeCombo.SelectedIndex);
    }

    private void SyncTimeZoneCombo()
    {
        suppressHintComboEvents = true;
        try
        {
            DateHintTimeZoneCombo.SelectedIndex = (int)(currentHintSettings?.TimeZoneMode ?? DateHintTimeZoneMode.Local);
        }
        finally
        {
            suppressHintComboEvents = false;
        }
    }

    private void OnTimeZoneComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressHintComboEvents || currentHintSettings is null || DateHintTimeZoneCombo.SelectedIndex < 0)
            return;

        currentHintSettings.SetTimeZoneMode((DateHintTimeZoneMode)DateHintTimeZoneCombo.SelectedIndex);
    }

    private void SyncExpandDepthCombo()
    {
        suppressExpandDepthComboEvent = true;
        try
        {
            ExpandDepthCombo.SelectedIndex = currentExpandDepth;
        }
        finally
        {
            suppressExpandDepthComboEvent = false;
        }
    }

    /// <summary>
    /// Persists the new default-expand depth for future file opens, and applies it live to
    /// whichever JSON/NDJSON tree is currently on screen (see JsonVisibleRowCollection's
    /// depth-vs-override expand model - this only touches containers the user hasn't
    /// explicitly toggled themselves).
    /// </summary>
    private void OnExpandDepthComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (suppressExpandDepthComboEvent || ExpandDepthCombo.SelectedIndex < 0)
            return;

        currentExpandDepth = ExpandDepthCombo.SelectedIndex;
        ExpandDepthPreference.Save(currentExpandDepth);

        if (ContentArea.Content is JsonView { DataContext: JsonViewModel jsonVm })
            jsonVm.SetDefaultExpandDepth(currentExpandDepth);
        else if (ContentArea.Content is NdJsonView { DataContext: NdJsonViewModel ndJsonVm })
            ndJsonVm.SetDefaultExpandDepth(currentExpandDepth);
    }


    /// <summary>
    /// Updates the status bar when a file's background indexing finishes or fails.
    /// Fire-and-forget from the UI thread; per the app's threading convention (see
    /// CLAUDE.md) the awaits resume on the UI thread, so controls are touched directly.
    /// </summary>
    private async Task MonitorIndexingCompletionAsync(IFileIndexer indexer, string filePath, int requestId,
        Action? showCompleted = null)
    {
        try
        {
            await indexer.IndexingTask;
        }
        catch
        {
            if (requestId == openRequestId)
                StatusText.Text = $"{filePath} — indexing failed";
            return;
        }

        if (requestId != openRequestId)
            return;

        if (showCompleted is not null)
            showCompleted();
        else
            StatusText.Text = $"{filePath} — {indexer.ItemCount:N0} {indexer.ItemNoun}";
    }

    private void ReloadRecentFiles()
    {
        // Rendering (linklike buttons, theme-reactive foreground, empty-state text) lives in
        // EmptyStateView's ItemsControl template; this only supplies the data.
        emptyStateView.SetRecentFiles(RecentFileHistory.Load()
            .Select(path => new RecentFileItem(path, Path.GetFileName(path)))
            .ToList());
    }

    private sealed class StatusProgressReporter : IProgressReporter
    {
        private const int BucketSize = 5;

        private readonly MainWindow window;
        private readonly string path;
        private readonly int requestId;
        private int lastBucket = -1;

        public StatusProgressReporter(MainWindow window, string path, int requestId)
        {
            this.window = window;
            this.path = path;
            this.requestId = requestId;
        }

        public void Report(string message, long? current = null, long? max = null)
        {
            if (requestId != window.openRequestId)
                return;

            string text = $"{message} {path}…";

            if (current.HasValue && max.HasValue && max.Value > 0)
            {
                int percent = (int)Math.Min(100, (current.Value * 100L) / max.Value);

                // Only act once per 5% step - a raw byte-offset stream would otherwise
                // post to the UI thread far more often than the status text can usefully
                // change.
                int bucket = percent / BucketSize;
                if (bucket == lastBucket)
                    return;

                lastBucket = bucket;
                text += $" ({percent}%)";
            }

            Dispatcher.UIThread.Post(() => window.StatusText.Text = text);
        }
    }
}
