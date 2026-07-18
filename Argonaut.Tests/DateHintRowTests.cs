using System.Collections.Specialized;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies date hints flow from IValueHintProvider into JsonRow.Hint via BuildRow, and that
/// DateHintSettings changes correctly invalidate the realized-row cache.
/// </summary>
public class DateHintRowTests
{
    private const string Json = "{\"name\":\"x\",\"short\":123,\"ts\":1709305509}";

    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    private static int FindTokenIndex(JsonStructureIndex index, System.Func<JsonTokenInfo, bool> predicate)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            if (predicate(index.GetToken(i)))
                return i;
        }

        throw new System.InvalidOperationException("Token not found.");
    }

    [Fact]
    public void DefaultScheme_DecodesQualifyingNumber_LeavesOthersNull()
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var settings = new DateHintSettings();
            settings.SetUserDefault(DateDecodingScheme.JsSeconds);
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 10);
                int shortTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 3);
                int stringTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.String);

                var tsPosition = rows.FindVisiblePosition(tsTokenIndex)!.Value;
                var shortPosition = rows.FindVisiblePosition(shortTokenIndex)!.Value;
                var stringPosition = rows.FindVisiblePosition(stringTokenIndex)!.Value;

                Assert.NotNull(((JsonRow)rows[tsPosition]!).Hint);
                Assert.Null(((JsonRow)rows[shortPosition]!).Hint);
                Assert.Null(((JsonRow)rows[stringPosition]!).Hint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void SchemeOff_AllHintsNull()
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var settings = new DateHintSettings(); // stays Off
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 10);
                var position = rows.FindVisiblePosition(tsTokenIndex)!.Value;

                Assert.Null(((JsonRow)rows[position]!).Hint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void TimeZoneModeChange_ReDecodesHintAndFiresReset()
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var settings = new DateHintSettings();
            settings.SetUserDefault(DateDecodingScheme.JsSeconds); // defaults to TimeZoneMode.Local
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 10);
                var position = rows.FindVisiblePosition(tsTokenIndex)!.Value;
                string? localHint = ((JsonRow)rows[position]!).Hint;
                Assert.Contains("[local", localHint);

                int resetCount = 0;
                rows.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Reset)
                        resetCount++;
                };

                settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);

                Assert.Equal(1, resetCount);
                string? utcHint = ((JsonRow)rows[position]!).Hint;
                Assert.EndsWith("[UTC]", utcHint);
                Assert.NotEqual(localHint, utcHint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void SchemeChange_InvalidatesCacheAndFiresReset()
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var settings = new DateHintSettings();
            settings.SetUserDefault(DateDecodingScheme.JsSeconds);
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 10);
                var position = rows.FindVisiblePosition(tsTokenIndex)!.Value;
                string? beforeHint = ((JsonRow)rows[position]!).Hint;

                int resetCount = 0;
                rows.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Reset)
                        resetCount++;
                };

                settings.SetUserDefault(DateDecodingScheme.JsMilliseconds);

                Assert.Equal(1, resetCount);
                string? afterHint = ((JsonRow)rows[position]!).Hint;
                Assert.NotEqual(beforeHint, afterHint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void TokenOverride_DecodesOnlyThatToken()
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var settings = new DateHintSettings();
            settings.SetUserDefault(DateDecodingScheme.JsSeconds);
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 10);
                var position = rows.FindVisiblePosition(tsTokenIndex)!.Value;
                string? defaultHint = ((JsonRow)rows[position]!).Hint;

                settings.SetTokenOverride(tsTokenIndex, DateDecodingScheme.KeepaMinutes);
                string? overriddenHint = ((JsonRow)rows[position]!).Hint;
                Assert.NotEqual(defaultHint, overriddenHint);

                settings.SetTokenOverride(tsTokenIndex, null);
                string? restoredHint = ((JsonRow)rows[position]!).Hint;
                Assert.Equal(defaultHint, restoredHint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void PerTokenOff_RendersEmDashPlaceholder()
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var settings = new DateHintSettings();
            settings.SetUserDefault(DateDecodingScheme.JsSeconds);
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number && t.Length == 10);
                settings.SetTokenOverride(tsTokenIndex, DateDecodingScheme.Off);

                var position = rows.FindVisiblePosition(tsTokenIndex)!.Value;
                Assert.Equal("—", ((JsonRow)rows[position]!).Hint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void OutOfRangeOverride_RendersOutOfRangePlaceholder()
    {
        const string json = "{\"ts\":1709305509000}"; // 13-digit JS-ms sized value
        var (index, mmap, path) = BuildIndex(json);
        try
        {
            var settings = new DateHintSettings();
            settings.SetUserDefault(DateDecodingScheme.JsMilliseconds);
            var rows = new JsonVisibleRowCollection(index, mmap, new IValueHintProvider[] { new DateHintProvider(settings) });
            try
            {
                int tsTokenIndex = FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number);
                settings.SetTokenOverride(tsTokenIndex, DateDecodingScheme.KeepaMinutes);

                var position = rows.FindVisiblePosition(tsTokenIndex)!.Value;
                Assert.Equal("out of range", ((JsonRow)rows[position]!).Hint);
            }
            finally
            {
                rows.Dispose();
            }
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
