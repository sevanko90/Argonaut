using System;
using System.Threading.Tasks;
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
    private readonly Action jumpToFailureLocation;

    public IncompatibleViewModel(string filePath, string attemptedViewName, IndexFailure? failure,
        Action openAsRawText, Action jumpToFailureLocation)
    {
        FilePath = filePath;
        AttemptedViewName = attemptedViewName;
        IndexFailure = failure;
        this.openAsRawText = openAsRawText;
        this.jumpToFailureLocation = jumpToFailureLocation;
    }

    public string FilePath { get; }

    /// <summary>Display name of the view the user tried to switch to (e.g. "JSON").</summary>
    public string AttemptedViewName { get; }

    public IndexFailure? IndexFailure { get; }

    /// <summary>Nothing is indexed for a placeholder - see <see cref="IDocumentViewModel.IndexingTask"/>.</summary>
    public Task IndexingTask => Task.CompletedTask;

    public string StatusText => $"{FilePath} — not compatible with the {AttemptedViewName} view";

    /// <summary>Whether the failure-detail panel (message + location) should be shown at all.</summary>
    public bool HasFailureDetail => IndexFailure is not null;

    /// <summary>See <see cref="IndexFailureFormatting.DescribeLocation"/>.</summary>
    public string LocationText => IndexFailure is { } failure ? IndexFailureFormatting.DescribeLocation(failure) : string.Empty;

    /// <summary>Whether <see cref="LocationText"/> has anything to show.</summary>
    public bool HasLocationText => LocationText.Length > 0;

    /// <summary>
    /// Whether the location can actually be jumped to in the raw viewer - requires a byte
    /// offset, not just a line number (the raw viewer navigates by offset; see
    /// <see cref="JumpToFailureLocation"/>).
    /// </summary>
    public bool CanJumpToFailureLocation => IndexFailure?.ByteOffset is not null;

    /// <summary>Whether <see cref="LocationText"/> should render as plain (non-clickable) text -
    /// has something to show, but nothing to jump to.</summary>
    public bool ShowPlainLocationText => HasLocationText && !CanJumpToFailureLocation;

    public object? Toolbar => null;

    public bool CanHandleFileType(FileTypeDetector.FileKind fileType) => false;

    public ISearchNavigator? CreateSearchNavigator() => null;

    /// <summary>Invoked by the "Open as raw text" button.</summary>
    public void OpenAsRawText() => openAsRawText();

    /// <summary>Invoked by the location link - switches to raw text and jumps to the failure's byte offset.</summary>
    public void JumpToFailureLocation() => jumpToFailureLocation();

    public void Dispose()
    {
        // No backing MMapFile/session - nothing to release.
    }
}
