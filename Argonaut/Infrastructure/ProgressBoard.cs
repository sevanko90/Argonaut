using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// The work in progress the app shows in its progress bar - indexing, re-indexing, searching,
/// comparing, saving - and the rules for when that bar appears. Anything that runs long enough to
/// be worth watching calls <see cref="Begin"/> and reports through the entry it gets back; the
/// main window draws <see cref="Shown"/>.
///
/// Most of these operations are instant on ordinary files and only take visible time on huge
/// ones, so a bar that appeared for every one of them would flicker. Two rules prevent that, and
/// they are the whole point of this class:
///
///   <b>Show late.</b> Nothing appears until an operation has run for <see cref="ShowAfter"/>.
///   Anything faster is never shown at all, which is the common case.
///
///   <b>Once shown, stay.</b> An entry that has appeared stays for at least <see cref="ShowAtLeast"/>
///   even if its work finishes a moment later, so the bar is never a flash.
///
/// When the last entry goes, the bar fades out (<see cref="IsVisible"/>) and the entries are
/// cleared only after <see cref="FadeOut"/>, so the text does not vanish from a bar still fading.
///
/// The rules are applied by <see cref="Tick"/>, which the main window calls on a timer while
/// <see cref="HasWork"/> - no timer runs when nothing is. Time comes from a
/// <see cref="TimeProvider"/> so tests can step it.
///
/// UI thread only, apart from <see cref="ProgressEntry.Report"/> and
/// <see cref="ProgressEntry.Finish"/>, which are safe from a background thread.
/// </summary>
public sealed class ProgressBoard : ObservableObject
{
    /// <summary>How long an operation runs before it is shown.</summary>
    public static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(450);

    /// <summary>The least time an entry stays once shown.</summary>
    public static readonly TimeSpan ShowAtLeast = TimeSpan.FromMilliseconds(700);

    /// <summary>How long the bar takes to fade; matches the transition in MainWindow.axaml.</summary>
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(200);

    private readonly TimeProvider clock;
    private readonly List<ProgressEntry> pending = new();
    private bool isVisible;
    private long? fadingSince;

    public ProgressBoard(TimeProvider clock)
    {
        this.clock = clock;
    }

    /// <summary>The board the app uses. Documents reach it here, the way they reach
    /// <see cref="ToastService"/>, so a load signature need not carry it; tests that care about
    /// timing construct their own.</summary>
    public static ProgressBoard Shared { get; } = new(TimeProvider.System);

    /// <summary>Raised by <see cref="Begin"/> with the new entry, so the main window can start
    /// ticking.</summary>
    public event EventHandler<ProgressEntry>? WorkStarted;

    /// <summary>The entries the bar is showing, in the order they began.</summary>
    public ObservableCollection<ProgressEntry> Shown { get; } = new();

    /// <summary>Whether the bar should be showing. False while it fades out after its last entry
    /// has gone, which is why it is separate from <see cref="Shown"/> being non-empty.</summary>
    public bool IsVisible
    {
        get => this.isVisible;
        private set => SetField(ref this.isVisible, value);
    }

    /// <summary>True while anything is pending, shown, or fading - the only time
    /// <see cref="Tick"/> has anything to do.</summary>
    public bool HasWork => this.pending.Count > 0 || this.Shown.Count > 0;

    /// <summary>
    /// Starts tracking an operation called <paramref name="title"/> ("Indexing orders.json").
    /// <paramref name="requestStop"/>, when given, is what the bar's Stop button does - it must
    /// be a request (cooperative, returns at once); the operation still calls
    /// <see cref="ProgressEntry.Finish"/> when it has actually stopped.
    /// </summary>
    public ProgressEntry Begin(string title, Action? requestStop = null)
    {
        var entry = new ProgressEntry(title, requestStop, this.clock.GetTimestamp());
        this.pending.Add(entry);
        WorkStarted?.Invoke(this, entry);
        return entry;
    }

    /// <summary>Applies the show and hide rules at the current time.</summary>
    public void Tick()
    {
        long now = this.clock.GetTimestamp();

        for (int i = this.pending.Count - 1; i >= 0; i--)
        {
            var entry = this.pending[i];
            if (entry.IsFinished)
            {
                // Done before anyone needed to see it: the common case, and never shown.
                this.pending.RemoveAt(i);
            }
            else if (this.clock.GetElapsedTime(entry.BeganAt, now) >= ShowAfter)
            {
                this.pending.RemoveAt(i);
                entry.ShownAt = now;
                Shown.Add(entry);
            }
        }

        bool anyStaying = false;
        foreach (var entry in Shown)
        {
            if (!CanLeave(entry, now))
                anyStaying = true;
        }

        if (anyStaying)
        {
            // Entries that may go do so at once while others keep the bar up.
            for (int i = Shown.Count - 1; i >= 0; i--)
            {
                if (CanLeave(Shown[i], now))
                    Shown.RemoveAt(i);
            }

            this.fadingSince = null;
            IsVisible = true;
            return;
        }

        if (Shown.Count == 0)
        {
            IsVisible = false;
            return;
        }

        // Everything shown may go: fade the bar with its text still in it, then clear it.
        this.fadingSince ??= now;
        IsVisible = false;
        if (this.clock.GetElapsedTime(this.fadingSince.Value, now) >= FadeOut)
        {
            Shown.Clear();
            this.fadingSince = null;
        }
    }

    private bool CanLeave(ProgressEntry entry, long now) =>
        entry.IsFinished && this.clock.GetElapsedTime(entry.ShownAt, now) >= ShowAtLeast;
}

/// <summary>
/// One operation on a <see cref="ProgressBoard"/>, and the <see cref="IProgressReporter"/> the
/// operation reports through. The one place a report becomes a percentage: producers hand over
/// raw offsets, and this decides how finely they are shown. <see cref="Percent"/> is updated by a
/// post to the UI thread only when it has moved by <see cref="PercentStep"/>, so a producer
/// reporting often costs the UI thread nothing extra.
/// </summary>
public sealed class ProgressEntry : ObservableObject, IProgressReporter
{
    /// <summary>The smallest change in <see cref="Percent"/> worth showing - and so the most
    /// posts to the UI thread one operation can make (100 / step).</summary>
    public const int PercentStep = 1;

    private readonly Action? requestStop;
    private int? percent;
    private int lastReported = -1;
    private volatile bool isFinished;
    private bool stopRequested;

    internal ProgressEntry(string title, Action? requestStop, long beganAt)
    {
        Title = title;
        this.requestStop = requestStop;
        BeganAt = beganAt;
    }

    /// <summary>What is happening, as the bar names it.</summary>
    public string Title { get; }

    /// <summary>How far along, or null while no total is known.</summary>
    public int? Percent
    {
        get => this.percent;
        private set
        {
            if (SetField(ref this.percent, value))
                OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    /// <summary>True while there is no percentage to show, so the meter animates instead.</summary>
    public bool IsIndeterminate => this.percent is null;

    /// <summary>Whether the bar offers a Stop button for this operation.</summary>
    public bool CanStop => this.requestStop is not null && !this.stopRequested && !this.isFinished;

    /// <summary>True once the operation has ended, however it ended.</summary>
    public bool IsFinished => this.isFinished;

    internal long BeganAt { get; }

    internal long ShownAt { get; set; }

    /// <summary>The Stop button. Asks once; the operation finishes itself when it has stopped.</summary>
    public void RequestStop()
    {
        if (!CanStop)
            return;

        this.stopRequested = true;
        OnPropertyChanged(nameof(CanStop));
        this.requestStop!();
    }

    /// <summary>Marks the operation over. Idempotent, and safe from any thread; the board removes
    /// the entry on its next tick, subject to its minimum display time.</summary>
    public void Finish() => this.isFinished = true;

    /// <summary>Finishes this entry when <paramref name="work"/> completes, however it completes.</summary>
    public async void FinishWhen(Task work)
    {
        try
        {
            await work;
        }
        catch
        {
            // Failed or cancelled is still over; the operation reports why elsewhere.
        }

        Finish();
    }

    /// <summary>Called from the scan thread. <paramref name="message"/> is not shown yet - the
    /// entry's <see cref="Title"/> already says what is happening, in the starter's words. It is
    /// kept for work with sub-stages ("Indexing", then "Comparing"), which an entry could show
    /// under its title.</summary>
    public void Report(string message, long? current = null, long? max = null)
    {
        if (this.isFinished || current is not long done || max is not long total || total <= 0)
            return;

        int value = (int)Math.Clamp(done * 100L / total, 0, 100) / PercentStep * PercentStep;
        if (value == this.lastReported)
            return;

        this.lastReported = value;
        ProgressPost.ToUiThread(() =>
        {
            if (!this.isFinished)
                Percent = value;
        });
    }
}
