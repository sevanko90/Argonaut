using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JsonViewerCore.Features.Json;
using JsonViewerCore.Features.NdJson;
using JsonViewerCore.Infrastructure;
using System.Threading.Tasks;

namespace JsonViewerCore.Shell;

public partial class MainWindow : Window
{
    private const string DefaultTitle = "BigJsonViewer";

    private readonly EmptyStateView emptyStateView = new();
    private NdJsonViewModel? currentNdJsonViewModel;
    private int openRequestId;
    private string? currentFilePath;

    public MainWindow()
    {
        InitializeComponent();

        Title = DefaultTitle;
        emptyStateView.ChooseFileRequested += async (_, _) => await BrowseForFile();
        ContentArea.Content = emptyStateView;
        ReloadRecentFiles();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnCloseFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseFile();
    }

    private void CloseFile()
    {
        ++openRequestId;
        DetachNdJsonViewModel();
        currentFilePath = null;
        ContentArea.Content = emptyStateView;
        FileHeaderBar.IsVisible = false;
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

    private async Task BrowseForFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open JSON or NDJSON file",
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
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath))
            return;

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
        catch
        {
            return;
        }

        switch (fileType)
        {
            case FileTypeDetector.FileKind.NotJson:
                // can't do anything with it
                break;
            case FileTypeDetector.FileKind.Json:
            {
                DetachNdJsonViewModel();
                StatusText.Text = $"Indexing {normalizedPath}… 0%";

                var reporter = new StatusProgressReporter(this, normalizedPath, requestId);
                var vm = new JsonViewModel();
                await vm.LoadAsync(normalizedPath, reporter);
                if (requestId != openRequestId)
                    return;

                ContentArea.Content = new JsonView { DataContext = vm };
                currentFilePath = normalizedPath;
                ShowFileHeader(normalizedPath);
                RecentFileHistory.Add(normalizedPath);
                ReloadRecentFiles();
                StatusText.Text = $"{normalizedPath} — {vm.TokenCount:N0} tokens indexed so far";
                _ = MonitorJsonCompletionAsync(vm, requestId);
                break;
            }
            case FileTypeDetector.FileKind.Ndjson:
            {
                DetachNdJsonViewModel();
                StatusText.Text = $"Indexing {normalizedPath}… 0%";

                var reporter = new StatusProgressReporter(this, normalizedPath, requestId);
                var vm = new NdJsonViewModel();
                await vm.LoadAsync(normalizedPath, reporter);
                if (requestId != openRequestId)
                    return;

                currentNdJsonViewModel = vm;
                currentNdJsonViewModel.PropertyChanged += OnNdJsonViewModelPropertyChanged;
                ContentArea.Content = new NdJsonView { DataContext = vm };
                currentFilePath = normalizedPath;
                ShowFileHeader(normalizedPath);
                RecentFileHistory.Add(normalizedPath);
                ReloadRecentFiles();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
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

    private void ShowFileHeader(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        FileNameText.Text = fileName;
        ToolTip.SetTip(FileNameText, filePath);
        FileHeaderBar.IsVisible = true;
        Title = $"{DefaultTitle} — {fileName}";
    }

    private void DetachNdJsonViewModel()
    {
        if (currentNdJsonViewModel is null)
            return;

        currentNdJsonViewModel.PropertyChanged -= OnNdJsonViewModelPropertyChanged;
        currentNdJsonViewModel = null;
    }


    private async Task MonitorNdJsonCompletionAsync(NdJsonViewModel vm, int requestId)
    {
        try
        {
            await vm.IndexingTask;
        }
        catch
        {
            if (requestId != openRequestId)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = $"{vm.FilePath} — indexing failed";
            });
            return;
        }

        if (requestId != openRequestId)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateNdJsonStatus(vm);
        });
    }

    private async Task MonitorJsonCompletionAsync(JsonViewModel vm, int requestId)
    {
        try
        {
            await vm.IndexingTask;
        }
        catch
        {
            if (requestId != openRequestId)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusText.Text = $"{vm.FilePath} — indexing failed";
            });
            return;
        }

        if (requestId != openRequestId)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusText.Text = $"{vm.FilePath} — {vm.TokenCount:N0} tokens";
        });
    }

    private void ReloadRecentFiles()
    {
        var recentFiles = RecentFileHistory.Load();
        var panel = emptyStateView.RecentFilesHost;
        panel.Children.Clear();

        if (recentFiles.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No recent files yet.",
                Opacity = 0.7
            });
            return;
        }

        foreach (var path in recentFiles)
        {
            var button = new Button
            {
                Content = Path.GetFileName(path),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = null,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = Avalonia.Media.Brushes.DodgerBlue,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            button.Classes.Add("linklike");
            button.Click += (_, _) => { _ = OpenPath(path); };

            panel.Children.Add(button);
        }
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
