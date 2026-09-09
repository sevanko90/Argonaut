# Headless test trap: a `Dispatch` body whose task nobody awaits passes silently

**Status:** no known broken tests. This is a footgun note for anyone writing a new headless UI
test. Written 2026-09-08 after falling into it twice in one sitting.

`HeadlessUnitTestSession` has these overloads, and no `Dispatch(Func<Task>)`:

```
Dispatch(Action, CancellationToken)
Dispatch<T>(Func<T>, CancellationToken)
Dispatch<T>(Func<Task<T>>, CancellationToken)
```

That shape decides whether your assertions are observed.

## The idiom that works — and why the `return true;` matters

Every headless test in this repo ends its dispatch body with a value:

```csharp
return session.Dispatch(async () =>
{
    // ... build a window, pump, assert ...
    return true;          // <-- load-bearing, not decoration
}, CancellationToken.None);
```

With that `return`, the lambda is `Func<Task<bool>>`, so it binds to
`Dispatch<T>(Func<Task<T>>)`, which unwraps the inner task and pumps until it completes. An
assertion failure anywhere in the body — including after an `await` — reaches the runner.

**Delete the `return true;` and the test silently stops asserting.** The lambda becomes
`async () => { }`, which is `Func<Task>`; with no matching overload it binds to
`Dispatch<T>(Func<T>)` with `T = Task`, and the call returns `Task<Task>`. xUnit awaits that as a
plain `Task`, which completes as soon as the body reaches its first `await` and hands back its
inner task. Everything after that — which is where the assertions live, because the body has to
pump the dispatcher before it can look at anything — runs unobserved.

The same trap applies to `Dispatch(Action, …)`: it returns a `Task`, and if you drop it on the
floor rather than returning it from the test, exceptions inside the body vanish.

## Verified both ways

A body that drops the returned task and calls `Assert.Fail()` was reported as **passed**. The
same test, changed only to `return` the dispatch task, was reported as **failed**. If you are
writing one of these, mutate an assertion once and confirm the runner actually goes red before
trusting it — that check is cheap and this failure mode is invisible.

All 17 existing `Dispatch(async …)` call sites across `TableGridVirtualizationTests`,
`RawViewVirtualizationTests`, `CsvRevealTests` and `StatusProgressHandoffTests` do return a value,
so they are awaited correctly. `TableGridHoverMarkTests` uses the synchronous `Dispatch(Action, …)`
form and returns the task for the same reason.

## Related

Scroll performance of the JSON array table, which is what turned this up:
`docs/json-array-table-scroll-perf.md`.
