using System;
using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using JsonViewerCore.Features.Json;
using JsonViewerCore.Features.NdJson;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Shell;

public partial class MainWindow : Window
{
    private NdJsonViewModel? currentNdJsonViewModel;

    public MainWindow()
    {
        InitializeComponent();
        ReloadRecentFiles();
    }

    private async void OnBrowse(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

        PathBox.Text = path;
        OpenPath(path);
    }

    private void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenPath(PathBox.Text);
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath))
            return;

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
                DetachNdJsonViewModel();
                ContentArea.Content = new JsonView { DataContext = new JsonViewModel(normalizedPath) };
                RecentFileHistory.Add(normalizedPath);
                ReloadRecentFiles();
                StatusText.Text = normalizedPath;
                break;
            case FileTypeDetector.FileKind.Ndjson:
            {
                DetachNdJsonViewModel();
                var vm = new NdJsonViewModel(normalizedPath);
                currentNdJsonViewModel = vm;
                currentNdJsonViewModel.PropertyChanged += OnNdJsonViewModelPropertyChanged;
                ContentArea.Content = new NdJsonView { DataContext = vm };
                RecentFileHistory.Add(normalizedPath);
                ReloadRecentFiles();
                UpdateNdJsonStatus(vm);
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

    private void DetachNdJsonViewModel()
    {
        if (currentNdJsonViewModel is null)
            return;

        currentNdJsonViewModel.PropertyChanged -= OnNdJsonViewModelPropertyChanged;
        currentNdJsonViewModel = null;
    }

    private void ReloadRecentFiles()
    {
        var recentFiles = RecentFileHistory.Load();
        RecentFilesPanel.Children.Clear();

        if (recentFiles.Count == 0)
        {
            RecentFilesPanel.Children.Add(new TextBlock
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
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = null,
                BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(0),
                Margin = new Avalonia.Thickness(0, 0, 12, 0),
                Foreground = Avalonia.Media.Brushes.DodgerBlue,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };

            button.Classes.Add("linklike");
            button.Click += (_, _) =>
            {
                PathBox.Text = path;
                OpenPath(path);
            };

            RecentFilesPanel.Children.Add(button);
        }
    }
}
