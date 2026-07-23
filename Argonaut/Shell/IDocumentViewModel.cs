using System;
using System.ComponentModel;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Shell;

/// <summary>
/// The shell's contract for one open document (JSON, NDJSON, CSV/TSV). Each document view
/// model owns its whole status-bar line (<see cref="StatusText"/>), including reacting to
/// its own selection changes and its background indexing completing or failing - the shell
/// only mirrors the current document's text.
///
/// Lifetime contract: instances are created and loaded by MainWindowViewModel, which owns
/// disposal. It disposes any instance that never becomes CurrentDocument (a stale open
/// superseded by a newer request, or a failed load), and it disposes the outgoing document
/// when CurrentDocument changes - crucially, before the swap, not after (see
/// MainWindowViewModel.SetCurrentDocument).
///
/// Disposing before the swap matters because setting CurrentDocument makes Avalonia tear down
/// the outgoing view, and that teardown enumerates the old ListBox's whole-file, mmap-backed
/// ItemsSource once. The collections report themselves empty once disposed (see
/// MemoryMappedFileLineCollection / JsonVisibleRowCollection / CsvRowCollection), so an
/// already-disposed document turns that walk into a no-op instead of a multi-second, whole-file
/// materialization that also read freed memory and crashed. The hosting view's
/// DetachedFromVisualTree handler also disposes its DataContext, as an idempotent safety net
/// for teardown paths the shell doesn't drive (e.g. window close); <see cref="IDisposable.Dispose"/>
/// is idempotent on every implementation, so the double-dispose is harmless.
/// </summary>
public interface IDocumentViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Full path of the loaded file.</summary>
    string FilePath { get; }

    /// <summary>The full status-bar line for this document; observable.</summary>
    string StatusText { get; }

    /// <summary>
    /// This document's header toolbar view model, or null for a document with no toolbar
    /// (e.g. CSV). The shell renders it via a DataTemplate keyed on the concrete type, in a
    /// region that swaps alongside the document itself - so <c>object?</c> is the honest
    /// contract here rather than a typed marker interface the shell never calls members on.
    /// Shares this document's lifetime: created during load, torn down with the document.
    /// </summary>
    object? Toolbar { get; }

    /// <summary>Creates the search navigator the shell attaches to its FindController.</summary>
    ISearchNavigator CreateSearchNavigator();

    /// <summary>
    /// Returns true if the VM can process the specified file type
    /// </summary>
    /// <param name="fileType">Type of file to query</param>
    /// <returns>True if the view model can process the specified file type</returns>
    bool CanHandleFileType(FileTypeDetector.FileKind fileType);
}
