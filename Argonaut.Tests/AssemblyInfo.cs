using Xunit;

// Run test collections one at a time.
//
// Two kinds of test in this assembly both reach for Avalonia's UI-thread dispatcher, and they
// cannot safely do so at the same time from different xUnit worker threads:
//
//   * The headless UI tests (RawViewVirtualizationTests, StatusProgressHandoffTests) start a
//     HeadlessUnitTestSession, whose application setup calls Dispatcher.VerifyAccess().
//   * The mmap-backed row collections construct a DispatcherTimer for their growth monitor
//     (see MemoryMappedFileLineCollection / CsvRowCollection / RawRowCollection /
//     JsonVisibleRowCollection), so every test that builds one touches Dispatcher.UIThread -
//     on whatever pool thread xUnit happened to run it on.
//
// Racing those produced an intermittent "The calling thread cannot access this object because
// a different thread owns it" out of the headless session's startup, dependent only on
// scheduling. The whole suite runs in about a second, so serializing collections costs nothing
// measurable and removes the entire failure mode rather than papering over one instance of it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
