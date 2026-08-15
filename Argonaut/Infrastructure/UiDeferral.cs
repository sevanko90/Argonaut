using System;
using Avalonia.Threading;

namespace Argonaut.Infrastructure;

/// <summary>
/// Runs UI work *after* the input event currently being dispatched has finished unwinding.
///
/// This is the one sanctioned exception to the app's "UI-originated flows never dispatch
/// explicitly" convention (see CLAUDE.md). It is not about threads - the caller is already on the
/// UI thread - it is about re-entrancy: a two-way binding setter on a selection property runs
/// *inside* Avalonia's selection commit for the pointer press that triggered it
/// (<c>SelectingItemsControl.UpdateSelection</c> holds an open batch update while it pushes the
/// new index through the binding). Anything that closes the popup hosting that control, or swaps
/// the collection bound to its ItemsSource, destroys the list the still-open commit is about to
/// index into, and the commit then throws:
///
///   System.ArgumentOutOfRangeException: Index was out of range ... (Parameter 'index')
///     at Avalonia.Controls.Selection.SelectedItems`1.GetEnumerator()+MoveNext()
///     at Avalonia.Controls.Primitives.SelectingItemsControl.UpdateSelection(...)
///
/// Unhandled, on the dispatcher's input path - it takes the process down. Deferring the side
/// effects by one dispatcher turn lets the commit finish against the list it started with.
/// </summary>
public static class UiDeferral
{
    /// <summary>
    /// Test seam: when set, receives the work instead of the dispatcher, so a view-model test can
    /// run dispatcher-free and still control when deferred work lands (same pattern as
    /// <see cref="AppDataPaths.RootOverride"/>). Null in production.
    /// </summary>
    internal static Action<Action>? PostOverride;

    /// <summary>
    /// Queues <paramref name="action"/> to run on the UI thread once the current dispatcher job
    /// (the input event being handled) has completed. Fire-and-forget by design - like every
    /// other <c>Post</c> in the app, nothing awaits it.
    /// </summary>
    public static void AfterCurrentInput(Action action)
    {
        if (PostOverride is { } post)
            post(action);
        else
            Dispatcher.UIThread.Post(action);
    }
}
