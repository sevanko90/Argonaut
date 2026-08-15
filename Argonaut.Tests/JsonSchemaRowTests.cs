using System.Collections.Specialized;
using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// End-to-end check that a bound schema reaches <see cref="JsonRow.SchemaTitle"/>/
/// <see cref="JsonRow.SchemaDescription"/> through the real pipeline: mapped file →
/// <see cref="JsonStructureIndex"/> → <see cref="JsonVisibleRowCollection"/>. Built on the same
/// temp-file fixture as <see cref="DateHintRowTests"/>.
/// </summary>
public class JsonSchemaRowTests
{
    private const string Json = """
        {"name":"widget","status":"a","ts":1709305509,"csv":[[1,2],[3,4]],"nested":{"deep":{"deeper":1}},"bare":"x","prose":"y","markup":"z"}
        """;

    private const string Schema = """
        {
          "properties": {
            "name": { "title": "Product name", "description": "What the thing is called." },
            "status": {
              "title": "Status",
              "oneOf": [ { "const": "a", "title": "Active", "description": "Currently sold." } ]
            },
            "ts": { "title": "Created" },
            "csv": {
              "title": "Series",
              "prefixItems": [ { "title": "First series" }, { "title": "Second series" } ]
            },
            "nested": { "title": "Nested" },
            "bare": { "description": "Only a description, no title." },
            "prose": { "description": "First line only.\n\nSecond paragraph that must not reach the row." },
            "markup": { "description": "Docs-site prose. </br></br> **NOTE**: not wanted on the row." }
          }
        }
        """;

    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    private static JsonSchemaDocument LoadSchema()
        => JsonSchemaLoader.TryParse(Schema) ?? throw new InvalidOperationException("Schema failed to load.");

    /// <summary>Token index of the member named <paramref name="name"/>, comparing the raw
    /// property-name bytes the index recorded.</summary>
    private static int FindMember(JsonStructureIndex index, MMapFile mmap, string name)
    {
        var wanted = Encoding.UTF8.GetBytes(name);
        for (int i = 0; i < index.TokenCount; i++)
        {
            var token = index.GetToken(i);
            if (token.NameLength == wanted.Length && mmap.GetSpan(token.NameOffset, token.NameLength).SequenceEqual(wanted))
                return i;
        }

        throw new InvalidOperationException($"Member '{name}' not found.");
    }

    private static JsonRow RowFor(JsonVisibleRowCollection rows, int tokenIndex)
    {
        int position = rows.FindVisiblePosition(tokenIndex) ?? throw new InvalidOperationException("Token is not visible.");
        return (JsonRow)rows[position]!;
    }

    private static void WithRows(Action<JsonStructureIndex, MMapFile, JsonVisibleRowCollection> body, JsonSchemaDocument? schema = null, DateHintSettings? hintSettings = null)
    {
        var (index, mmap, path) = BuildIndex(Json);
        try
        {
            var providers = hintSettings is null ? null : new IValueHintProvider[] { new DateHintProvider(hintSettings) };
            var rows = new JsonVisibleRowCollection(index, mmap, providers, defaultExpandDepth: 5);
            try
            {
                if (schema is not null)
                    rows.SetSchema(schema);

                body(index, mmap, rows);
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
    public void DescriptionOnlyMember_FallsBackToTheDescriptionAsItsLabel()
    {
        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "bare"));

            // Generated schemas document with `description` and no `title`, so a row that
            // rendered only titles would show nothing - and, since the label element carries the
            // tooltip, would hide the description too.
            Assert.Equal("Only a description, no title.", row.SchemaTitle);
            Assert.Equal("Only a description, no title.", row.SchemaDescription);
        }, LoadSchema());
    }

    [Fact]
    public void DescriptionFallback_UsesTheFirstLineOnly()
    {
        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "prose"));

            Assert.Equal("First line only.", row.SchemaTitle);

            // The tooltip keeps the whole thing.
            Assert.Contains("Second paragraph", row.SchemaDescription);
        }, LoadSchema());
    }

    [Fact]
    public void DescriptionFallback_StopsAtADocsSiteLineBreak()
    {
        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "markup"));

            // Generated docs break paragraphs with literal <br>/</br> as often as with a newline,
            // and the raw tag must never reach the row.
            Assert.Equal("Docs-site prose.", row.SchemaTitle);
        }, LoadSchema());
    }

    [Fact]
    public void ATitle_IsNeverReplacedByTheDescription()
    {
        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "name"));

            Assert.Equal("Product name", row.SchemaTitle);
        }, LoadSchema());
    }

    [Fact]
    public void ObjectMember_GetsTitleAndDescription()
    {
        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "name"));

            Assert.Equal("Product name", row.SchemaTitle);
            Assert.Equal("What the thing is called.", row.SchemaDescription);
        }, LoadSchema());
    }

    [Fact]
    public void PrefixItemsSlot_LabelsPositionalArrayElements()
    {
        WithRows((index, mmap, rows) =>
        {
            int csv = FindMember(index, mmap, "csv");
            int first = csv + 1;
            int second = index.GetToken(first).EndIndex + 1;

            Assert.Equal("Series", RowFor(rows, csv).SchemaTitle);
            Assert.Equal("First series", RowFor(rows, first).SchemaTitle);
            Assert.Equal("Second series", RowFor(rows, second).SchemaTitle);
        }, LoadSchema());
    }

    [Fact]
    public void EnumLabel_SupersedesNodeTitle()
    {
        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "status"));

            Assert.Equal("Active", row.SchemaTitle);
            Assert.Equal("Currently sold.", row.SchemaDescription);
        }, LoadSchema());
    }

    [Fact]
    public void SchemaRunningOut_LeavesDeeperRowsUnlabelled()
    {
        WithRows((index, mmap, rows) =>
        {
            Assert.Equal("Nested", RowFor(rows, FindMember(index, mmap, "nested")).SchemaTitle);
            Assert.Null(RowFor(rows, FindMember(index, mmap, "deep")).SchemaTitle);
            Assert.Null(RowFor(rows, FindMember(index, mmap, "deeper")).SchemaTitle);
        }, LoadSchema());
    }

    [Fact]
    public void NoSchema_LeavesEveryRowUnlabelled()
    {
        WithRows((index, mmap, rows) =>
        {
            Assert.Null(RowFor(rows, FindMember(index, mmap, "name")).SchemaTitle);
            Assert.Null(RowFor(rows, FindMember(index, mmap, "name")).SchemaDescription);
        });
    }

    [Fact]
    public void SetSchemaNull_ClearsTitles_AndResetsTheList()
    {
        WithRows((index, mmap, rows) =>
        {
            int name = FindMember(index, mmap, "name");
            Assert.Equal("Product name", RowFor(rows, name).SchemaTitle);

            var actions = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)rows).CollectionChanged += (_, e) => actions.Add(e.Action);

            rows.SetSchema(null);

            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
            Assert.Null(RowFor(rows, name).SchemaTitle);
        }, LoadSchema());
    }

    [Fact]
    public void SetSchema_WithSameInstance_IsANoOp()
    {
        var schema = LoadSchema();
        WithRows((index, mmap, rows) =>
        {
            var actions = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)rows).CollectionChanged += (_, e) => actions.Add(e.Action);

            rows.SetSchema(schema);

            Assert.Empty(actions);
        }, schema);
    }

    [Fact]
    public void DateHintAndSchemaTitle_CoexistOnOneRow()
    {
        var hintSettings = new DateHintSettings();
        hintSettings.SetUserDefault(DateDecodingScheme.JsSeconds);

        WithRows((index, mmap, rows) =>
        {
            var row = RowFor(rows, FindMember(index, mmap, "ts"));

            Assert.NotNull(row.Hint);
            Assert.Equal("Created", row.SchemaTitle);
        }, LoadSchema(), hintSettings);
    }
}
