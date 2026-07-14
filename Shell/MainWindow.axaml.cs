using System;
using Avalonia.Controls;
using JsonViewerCore.Features.Json;
using JsonViewerCore.Features.NdJson;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = PathBox.Text;
        if (string.IsNullOrWhiteSpace(path)) return;

        var fileType = FileTypeDetector.DetectFileType(path);

        switch (fileType)
        {
            case FileTypeDetector.FileKind.NotJson:
                // can't do anything with it
                break;
            case FileTypeDetector.FileKind.Json:
                ContentArea.Content = new JsonView { DataContext = new JsonViewModel(path) };
                break;
            case FileTypeDetector.FileKind.Ndjson:
                ContentArea.Content = new NdJsonView { DataContext = new NdJsonViewModel(path) };
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
            
    }
}