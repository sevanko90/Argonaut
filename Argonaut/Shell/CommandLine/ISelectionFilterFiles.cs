using System;
using System.IO;
using Argonaut.Engine.Selection;
using Avalonia.Threading;

namespace Argonaut.Shell.CommandLine;

/// <summary>
/// Reads and watches the filter files <c>--filter</c> names. Behind an interface so the shell
/// never opens a file itself: the app composes the real one, and tests hand the shell filters
/// from memory and say when they change.
/// </summary>
public interface ISelectionFilterFiles
{
    /// <summary>Reads the filter file at <paramref name="path"/>, its entries counting
    /// <paramref name="unit"/>.</summary>
    /// <exception cref="SelectionFilterException">An entry is not a range.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    SelectionFilter Read(string path, SelectionUnit unit);

    /// <summary>
    /// Calls <paramref name="changed"/> on the UI thread each time the file at
    /// <paramref name="path"/> has been written to and the writes have settled, until the
    /// returned watch is disposed. Called from the UI thread.
    /// </summary>
    IDisposable Watch(string path, Action changed);
}

/// <summary>Filter files read from disk, and watched there.</summary>
public sealed class SelectionFilterFiles : ISelectionFilterFiles
{
    /// <summary>How long the file must go unwritten before a change is reported: long enough
    /// that a writer part way through a burst is not read half-written, short enough that the
    /// view still seems to follow it.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    public SelectionFilter Read(string path, SelectionUnit unit)
    {
        // Shared for writing: whoever maintains the filter may hold it open while it is read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return SelectionFilter.Parse(reader, unit);
    }

    public IDisposable Watch(string path, Action changed) => new FileWatch(path, changed);

    /// <summary>
    /// A watcher on the file's folder, narrowed to its name - so a writer that replaces the file
    /// (write a temporary, rename it over) is seen as well as one that writes in place. Its
    /// events arrive on the thread pool and are posted to the UI thread, where a timer waits for
    /// them to stop.
    /// </summary>
    private sealed class FileWatch : IDisposable
    {
        private readonly FileSystemWatcher watcher;
        private readonly DispatcherTimer settle;
        private readonly Action changed;
        private bool disposed;

        public FileWatch(string path, Action changed)
        {
            this.changed = changed;
            this.settle = new DispatcherTimer { Interval = Settle };
            this.settle.Tick += OnSettled;

            string full = Path.GetFullPath(path);
            this.watcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            this.watcher.Changed += OnFileEvent;
            this.watcher.Created += OnFileEvent;
            this.watcher.Renamed += OnFileEvent;
            this.watcher.EnableRaisingEvents = true;
        }

        // On the thread pool.
        private void OnFileEvent(object sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(Restart);

        private void Restart()
        {
            if (this.disposed)
                return;

            this.settle.Stop();
            this.settle.Start();
        }

        private void OnSettled(object? sender, EventArgs e)
        {
            this.settle.Stop();
            if (!this.disposed)
                this.changed();
        }

        public void Dispose()
        {
            this.disposed = true;
            this.watcher.Dispose();
            this.settle.Stop();
            this.settle.Tick -= OnSettled;
        }
    }
}
