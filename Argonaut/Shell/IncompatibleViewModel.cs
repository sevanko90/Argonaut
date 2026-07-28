using System;
using System.Collections.Generic;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Shell;

/// <summary>
/// Placeholder document shown when the user forces a view onto a file it can't handle - either
/// <see cref="FileTypeDetector.IsPlausibleFor"/> rejected it pre-flight, or the indexer failed
/// before publishing any items. Has no backing <see cref="MMapFile"/>, so there is nothing to
/// search or tear down: <see cref="CreateSearchNavigator"/> returns null and <see cref="Dispose"/>
/// is a no-op.
/// </summary>
public sealed class IncompatibleViewModel : ObservableObject, IDocumentViewModel
{
    private readonly Action openAsRawText;

    public IncompatibleViewModel(string filePath, string attemptedViewName, IndexFailure? failure, Action openAsRawText)
    {
        FilePath = filePath;
        AttemptedViewName = attemptedViewName;
        IndexFailure = failure;
        this.openAsRawText = openAsRawText;
    }

    public string FilePath { get; }

    /// <summary>Display name of the view the user tried to switch to (e.g. "JSON").</summary>
    public string AttemptedViewName { get; }

    public IndexFailure? IndexFailure { get; }

    public string StatusText => $"{FilePath} — not compatible with the {AttemptedViewName} view";

    /// <summary>Whether the failure-detail panel (message + location) should be shown at all.</summary>
    public bool HasFailureDetail => IndexFailure is not null;

    /// <summary>
    /// "Line N, column M (byte K)"-style summary of where the scan stopped, omitting any
    /// parts the indexer couldn't supply; empty when there's no location info at all.
    /// </summary>
    public string LocationText
    {
        get
        {
            if (IndexFailure is not { } failure)
                return string.Empty;

            var parts = new List<string>();
            if (failure.Line is { } line)
                parts.Add(failure.Column is { } col ? $"Line {line:N0}, column {col:N0}" : $"Line {line:N0}");
            if (failure.ByteOffset is { } offset)
                parts.Add($"byte {offset:N0}");

            return parts.Count == 0 ? string.Empty : string.Join(" (", parts) + (parts.Count > 1 ? ")" : "");
        }
    }

    /// <summary>Whether <see cref="LocationText"/> has anything to show.</summary>
    public bool HasLocationText => LocationText.Length > 0;

    public object? Toolbar => null;

    public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

    public ISearchNavigator? CreateSearchNavigator() => null;

    /// <summary>Invoked by the "Open as raw text" button.</summary>
    public void OpenAsRawText() => openAsRawText();

    public void Dispose()
    {
        // No backing MMapFile/session - nothing to release.
    }
}
