using System.Text;
using System.Text.Json;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Tree;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Tree;

/// <summary>
/// What a JSON tree row says - names and values as written, collapsed summaries, array indices,
/// the "view as table" link - checked row for row against a <see cref="JsonModel"/> of the same
/// document; then the display cap and its notes, and decoded dates.
/// </summary>
public sealed class JsonTreePainterTests
{
    /// <summary>What the model says each row should say.</summary>
    private static void AssertRowsSayWhatTheModelSays(byte[] json, int defaultDepth)
    {
        var model = new JsonModel(json);
        var expand = new TreeExpandState(defaultDepth);
        var expected = model.Rows(expand);
        var actual = new JsonTreeHarness(json).Rows(expand);

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var node = model.Nodes[expected[i].Node];
            var row = actual[i];
            bool isArray = node.Kind == JsonTokenType.StartArray;
            bool parentIsArray = node.Parent >= 0 && model.Nodes[node.Parent].Kind == JsonTokenType.StartArray;

            if (expected[i].IsClose)
            {
                Assert.Equal(isArray ? "]" : "}", row.Value);
                Assert.Null(row.Name);
                Assert.Null(row.Marker);
                continue;
            }

            Assert.Equal(node.RawName, row.Name);
            Assert.Equal(parentIsArray ? node.Ordinal.ToString() : null, row.Marker);

            if (!node.IsContainer)
            {
                Assert.Equal(model.RawValue(expected[i].Node), row.Value);
                continue;
            }

            bool expanded = expand.IsExpanded(node.ValueStart, node.Depth);
            int count = node.Children.Count;
            string label = isArray ? "item" : "member";
            string summary = $"{(isArray ? "[" : "{")} {count} {label}{(count == 1 ? "" : "s")} {(isArray ? "]" : "}")}";
            Assert.Equal(expanded ? (isArray ? "[" : "{") : summary, row.Value);
            Assert.Equal(isArray && count > 0, row.LinkOf<ViewAsTableLink>() is not null);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 30)]
    public void NamesValuesSummariesAndIndicesAreWhatTheFileSays(int seed, int defaultDepth)
        => AssertRowsSayWhatTheModelSays(RandomJson.LargeContainers(new Random(seed), elements: 30), defaultDepth);

    /// <summary>Names and string values outside ASCII - raw multi-byte UTF-8 in many scripts,
    /// emoji with ZWJ sequences, flags and skin tones, combining marks, astral-plane letters and
    /// invisible characters - show as the text in the file. A property name is any JSON string, so
    /// names get the same coverage as values.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void TheUnicodeFixtureShowsAsTheTextInTheFile(int defaultDepth)
        => AssertRowsSayWhatTheModelSays(File.ReadAllBytes(Fixtures.UnicodeNamesAndValuesJson), defaultDepth);

    [Fact]
    public void TheUnicodeFixtureReallyContainsWhatTheTestClaims()
    {
        string text = File.ReadAllText(Fixtures.UnicodeNamesAndValuesJson, Encoding.UTF8);

        Assert.Contains("日本語", text);         // raw multi-byte name
        Assert.Contains("👨‍👩‍👧", text);          // ZWJ sequence
        Assert.Contains("𝔘𝔫𝔦𝔠𝔬𝔡𝔢", text);        // astral-plane letters in a name
        Assert.Contains("😀", text);            // an astral-plane emoji
        Assert.Contains("\"\":", text);          // the empty name
    }

    // ── The display cap ────────────────────────────────────────────────────────────────

    [Fact]
    public void ALongStringIsCutAndLinksToTheRawViewAtItsContent()
    {
        string payload = new('a', 5000);
        var tree = new JsonTreeHarness($"{{\"payload\":\"{payload}\"}}");
        var row = tree.Member("payload");

        // Opening quote + capped text + ellipsis; no closing quote on a cut string.
        Assert.Equal(DisplayText.MaxLength + 2, row.Value.Length);
        Assert.StartsWith("\"", row.Value);
        Assert.EndsWith("…", row.Value);

        var link = row.LinkOf<ViewInRawLink>()!.Value;
        Assert.Contains("truncated", link.Text);
        Assert.Contains("4.9 KB", link.Text);
        Assert.Equal(row.Row.Node.ValueStart + 1, ((ViewInRawLink)link.Link!).Offset);
    }

    [Fact]
    public void AShortValueIsWholeWithNoNote()
    {
        var row = new JsonTreeHarness("{\"payload\":\"short\"}").Member("payload");

        Assert.Equal("\"short\"", row.Value);
        Assert.Null(row.LinkOf<ViewInRawLink>());
        Assert.Null(row.Note);
    }

    [Fact]
    public void ACutMidCharacterBacksOffToTheCharacterBoundary()
    {
        // 'a' then 1000 two-byte 'é's puts every 'é' at an odd byte offset, so the cap (an even
        // offset) falls mid-character and must back off rather than decode a split sequence into
        // a replacement glyph.
        string payload = "a" + new string('é', 1000);
        var row = new JsonTreeHarness($"{{\"payload\":\"{payload}\"}}").Member("payload");

        Assert.EndsWith("…", row.Value);
        Assert.DoesNotContain('�', row.Value);
    }

    [Fact]
    public void ALongNameIsCutWithAPlainNote()
    {
        string name = new('k', 4000);
        var row = new JsonTreeHarness($"{{\"{name}\":1}}").Rows()[1];

        Assert.Equal(DisplayText.MaxLength + 1, row.Name!.Length);
        Assert.EndsWith("…", row.Name);

        // Only the name overflowed, not the value - nothing to jump to, so a plain note rather
        // than a "view in raw" link.
        Assert.Contains("name truncated", row.Note);
        Assert.Null(row.LinkOf<ViewInRawLink>());
    }

    [Fact]
    public void ALongNumberIsCut()
    {
        var json = new StringBuilder("{\"n\":1").Append('2', 3000).Append('}').ToString();
        var row = new JsonTreeHarness(json).Member("n");

        Assert.Equal(DisplayText.MaxLength + 1, row.Value.Length);
        Assert.EndsWith("…", row.Value);
        Assert.NotNull(row.LinkOf<ViewInRawLink>());
    }

    // ── Decoded dates ──────────────────────────────────────────────────────────────────

    private const string DateJson = "{\"name\":\"x\",\"short\":123,\"ts\":1709305509,\"list\":[1600000000,\"y\"]}";

    private static DateHintSettings JsSeconds()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsSeconds);
        return settings;
    }

    [Fact]
    public void TheDefaultSchemeDecodesQualifyingNumbersOnly()
    {
        var tree = new JsonTreeHarness(DateJson, JsSeconds());

        Assert.NotNull(tree.Member("ts").DateHint);
        Assert.Null(tree.Member("short").DateHint);
        Assert.Null(tree.Member("name").DateHint);
        Assert.NotNull(tree.Rows().Single(r => r.Value == "1600000000").DateHint);
    }

    [Fact]
    public void AHintLinksToItsValue()
    {
        var row = new JsonTreeHarness(DateJson, JsSeconds()).Member("ts");

        var link = row.LinkOf<DateSchemeLink>()!.Value;
        Assert.Equal(row.Row.Node.ValueStart, ((DateSchemeLink)link.Link!).ValueOffset);
    }

    [Fact]
    public void WithTheSchemeOffThereAreNoHints()
        => Assert.All(new JsonTreeHarness(DateJson, new DateHintSettings()).Rows(), r => Assert.Null(r.DateHint));

    [Fact]
    public void ChangingTheTimeZoneModeChangesTheHint()
    {
        var settings = JsSeconds(); // local time by default
        var tree = new JsonTreeHarness(DateJson, settings);
        string? local = tree.Member("ts").DateHint;
        Assert.Contains("[local", local);

        settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);

        string? utc = tree.Member("ts").DateHint;
        Assert.EndsWith("[UTC]", utc);
        Assert.NotEqual(local, utc);
    }

    [Fact]
    public void ChangingTheSchemeChangesTheHint()
    {
        var settings = JsSeconds();
        var tree = new JsonTreeHarness(DateJson, settings);
        string? before = tree.Member("ts").DateHint;

        settings.SetUserDefault(DateDecodingScheme.JsMilliseconds);

        Assert.NotEqual(before, tree.Member("ts").DateHint);
    }

    [Fact]
    public void AValueOverrideDecodesOnlyThatValue()
    {
        var settings = JsSeconds();
        var tree = new JsonTreeHarness(DateJson, settings);
        var ts = tree.Member("ts");
        string? listHint = tree.Rows().Single(r => r.Value == "1600000000").DateHint;

        settings.SetValueOverride(ts.Row.Node.ValueStart, DateDecodingScheme.KeepaMinutes);
        Assert.NotEqual(ts.DateHint, tree.Member("ts").DateHint);
        Assert.Equal(listHint, tree.Rows().Single(r => r.Value == "1600000000").DateHint);

        settings.SetValueOverride(ts.Row.Node.ValueStart, null);
        Assert.Equal(ts.DateHint, tree.Member("ts").DateHint);
    }

    [Fact]
    public void AValueTurnedOffShowsADash()
    {
        var settings = JsSeconds();
        var tree = new JsonTreeHarness(DateJson, settings);
        settings.SetValueOverride(tree.Member("ts").Row.Node.ValueStart, DateDecodingScheme.Off);

        Assert.Equal("—", tree.Member("ts").DateHint);
    }

    [Fact]
    public void AnOverrideOutOfRangeSaysSo()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsMilliseconds);
        var tree = new JsonTreeHarness("{\"ts\":1709305509000}", settings);
        settings.SetValueOverride(tree.Member("ts").Row.Node.ValueStart, DateDecodingScheme.KeepaMinutes);

        Assert.Equal("out of range", tree.Member("ts").DateHint);
    }
}
