using System;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// Everything <see cref="IndexedDocumentViewModel"/> needs from the thing a document is reading:
/// the half of a session's lifetime the teardown ordering is written in terms of (stop the
/// background work, then release what it was reading), plus the completion signal and outcome
/// the status line is driven from. Implemented by <see cref="IndexedSourceSession{TIndex}"/>,
/// <see cref="Argonaut.Features.Raw.RawIndexSession"/> and
/// <see cref="Argonaut.Features.Json.Diff.JsonDiffSession"/>.
///
/// Deliberately NOT an index. Two of the three implementations own an
/// <see cref="IFileIndexer"/> and one (the diff) owns something else entirely, so a base class
/// reaching for <c>session.Index</c> can only do it through a nullable accessor plus virtual
/// escape hatches for the odd one out. Everything such a base actually wants from an index is a
/// task to await and a failure to report - so those are the members, stated at the level all
/// three sessions can answer them. Do not add an index here.
/// </summary>
public interface IDocumentSession : IDisposable
{
    /// <summary>
    /// Cancelled when this document begins tearing down - <see cref="RequestStop"/> or
    /// <see cref="IDisposable.Dispose"/>, whichever comes first. UI-thread work that outlives
    /// a single dispatcher turn while reading this document (a find reveal awaiting index
    /// coverage) links this, so it stops instead of touching a released mapping.
    ///
    /// Named for the moment it fires rather than for its type, per CLAUDE.md's naming
    /// convention: a caller needs to know WHEN, not WHAT.
    /// </summary>
    CancellationToken TearingDown { get; }

    /// <summary>
    /// Asks every background task this session started - indexing scans and registered
    /// dependent readers alike - to stop. A REQUEST: cooperative, returns immediately, and
    /// nothing has let go of the mapping yet when it does. Dispose is what waits. Idempotent,
    /// including after Dispose.
    /// </summary>
    void RequestStop();

    /// <summary>
    /// The background work whose completion this document reacts to. Faults on failure AND on
    /// cancellation - <see cref="Failure"/> is what tells the two apart afterwards.
    ///
    /// Read LIVE, never cached: <see cref="Argonaut.Features.Raw.RawIndexSession"/> replaces it
    /// when a wrap-width change re-indexes the same mapping. That is exactly what lets the
    /// completion monitor detect a retired scan - it compares the task it awaited against this
    /// property, so a task that is no longer the session's own is known to be stale.
    /// </summary>
    Task IndexingTask { get; }

    /// <summary>
    /// Why <see cref="IndexingTask"/> stopped early, or null. Null covers BOTH "ran to
    /// completion" and "was cancelled" - the task faults either way, so a monitor that has
    /// already caught the fault reads null here as cancellation.
    /// </summary>
    IndexFailure? Failure { get; }
}
