using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Tests.Engine.Settings;

/// <summary>
/// The settings file: one shared block per type, read lazily from what was stored, and written
/// only by <see cref="SettingsStore.Save"/>. Each test owns its own file, so none of this needs
/// the <c>AppDataPaths</c> collection.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(dir, "settings.json");

    public void Dispose()
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void Get_WithNothingStored_ReturnsDefaults()
    {
        var store = SettingsStore.Open(FilePath);

        Assert.Equal(7, store.Get<Counter>().Count);
    }

    [Fact]
    public void Get_ReturnsTheSameInstanceEveryTime()
    {
        var store = SettingsStore.InMemory();

        Assert.Same(store.Get<Counter>(), store.Get<Counter>());
    }

    [Fact]
    public void Save_ThenReopen_RoundTripsTheValue()
    {
        var store = SettingsStore.Open(FilePath);
        store.Get<Counter>().Count = 42;
        store.Save();

        Assert.Equal(42, SettingsStore.Open(FilePath).Get<Counter>().Count);
    }

    [Fact]
    public void NothingIsWritten_UntilSave()
    {
        var store = SettingsStore.Open(FilePath);
        store.Get<Counter>().Count = 42;

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_KeepsKeysNobodyAskedFor()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, """{ "fromANewerBuild": { "x": 1 }, "counter": { "Count": 3 } }""");

        var store = SettingsStore.Open(FilePath);
        store.Get<Counter>().Count = 4;
        store.Save();

        string written = File.ReadAllText(FilePath);
        Assert.Contains("fromANewerBuild", written);
        Assert.Equal(4, SettingsStore.Open(FilePath).Get<Counter>().Count);
    }

    [Fact]
    public void CorruptFile_StartsFromDefaults()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Equal(7, SettingsStore.Open(FilePath).Get<Counter>().Count);
    }

    [Fact]
    public void UnreadableBlock_FallsBackToItsDefault_WithoutLosingOthers()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, """{ "counter": { "Count": "not a number" }, "label": { "Text": "kept" } }""");

        var store = SettingsStore.Open(FilePath);

        Assert.Equal(7, store.Get<Counter>().Count);
        Assert.Equal("kept", store.Get<Label>().Text);
    }

    [Fact]
    public void Load_GoesThroughTheSetters_SoStoredValuesAreNormalised()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, """{ "counter": { "Count": 500 } }""");

        Assert.Equal(Counter.Max, SettingsStore.Open(FilePath).Get<Counter>().Count);
    }

    [Fact]
    public void TwoTypesClaimingOneKey_Throws()
    {
        var store = SettingsStore.InMemory();
        store.Get<Counter>();

        Assert.Throws<InvalidOperationException>(() => store.Get<ImpostorCounter>());
    }

    [Fact]
    public void InMemory_SaveTouchesNoDisk()
    {
        var store = SettingsStore.InMemory();
        store.Get<Counter>().Count = 1;
        store.Save();

        Assert.False(Directory.Exists(dir));
    }

    internal sealed class Counter : ISettingsBlock<Counter>
    {
        public const int Max = 100;

        public static string Key => "counter";

        public static JsonTypeInfo<Counter> JsonTypeInfo => TestSettingsJson.Default.Counter;

        public int Count
        {
            get;
            set => field = Math.Min(value, Max);
        } = 7;
    }

    internal sealed class Label : ISettingsBlock<Label>
    {
        public static string Key => "label";

        public static JsonTypeInfo<Label> JsonTypeInfo => TestSettingsJson.Default.Label;

        public string Text { get; set; } = "";
    }

    internal sealed class ImpostorCounter : ISettingsBlock<ImpostorCounter>
    {
        public static string Key => "counter";

        public static JsonTypeInfo<ImpostorCounter> JsonTypeInfo => TestSettingsJson.Default.ImpostorCounter;
    }
}

[JsonSerializable(typeof(SettingsStoreTests.Counter))]
[JsonSerializable(typeof(SettingsStoreTests.Label))]
[JsonSerializable(typeof(SettingsStoreTests.ImpostorCounter))]
internal sealed partial class TestSettingsJson : JsonSerializerContext;
