using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Covers the schema-type picker flyout: its two-section list, the filter, and the selection
/// rules that stop the ListBox's own churn from unbinding the user's choice.
/// </summary>
[Collection("AppDataPaths")]
public sealed class SchemaRootPickerViewModelTests : IDisposable
{
    private readonly string settingsRoot;

    /// <summary>Picking a type acts a dispatcher turn later (see <see cref="UiDeferral"/>); this
    /// stands in for that turn.</summary>
    private readonly DeferredUiScope ui = new();

    public SchemaRootPickerViewModelTests()
    {
        settingsRoot = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
        AppDataPaths.RootOverride = settingsRoot;
    }

    public void Dispose()
    {
        ui.Dispose();
        AppDataPaths.RootOverride = null;
        try { if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private const string ApiSchema = """
        {
          "openapi": "3.0.3",
          "components": {
            "schemas": {
              "Booking": {
                "description": "A booking and everything on it.",
                "properties": { "reference": {}, "passengers": {}, "flights": {} }
              },
              "Address": {
                "description": "Describes an address",
                "properties": { "line1": {}, "city": {} }
              },
              "Airport": { "properties": { "iata": {}, "name": {} } }
            }
          }
        }
        """;

    private static async Task<JsonSchemaSettings> BoundAsync(string json = ApiSchema)
    {
        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);

        var settings = new JsonSchemaSettings();
        await settings.SelectAsync(new SchemaCatalogEntry("api", path, IsUser: true));
        return settings;
    }

    private static IReadOnlyList<byte[]> Keys(params string[] names)
        => names.Select(Encoding.UTF8.GetBytes).ToArray();

    private static void Match(JsonSchemaSettings settings, params string[] documentKeys)
        => settings.SetRootMatches(JsonSchemaRootMatcher.Rank(settings.Document!, Keys(documentKeys)));

    private static string[] Names(SchemaRootPickerViewModel picker)
        => picker.Picks.Select(p => p.Name).ToArray();

    /// <summary>Picks a type the way the list does, then lets the deferred work run.</summary>
    private void Pick(SchemaRootPickerViewModel picker, string name)
    {
        picker.SelectedPick = picker.Picks.Single(p => p.Name == name);
        ui.Pump();
    }

    [Fact]
    public async Task WithoutScores_ListsTheUnbindEntryThenEveryType()
    {
        var picker = new SchemaRootPickerViewModel(await BoundAsync());

        Assert.Equal(
            new[]
            {
                $"{SchemaRootPickerViewModel.AllTypesHeader} (3)",
                "Address", "Airport", "Booking"
            },
            Names(picker));
    }

    [Fact]
    public async Task WithScores_LiftsTheLikelyMatchAboveTheFullList()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "reference", "passengers", "flights");

        Assert.Equal(
            new[]
            {
                // The winner sits above the sections, chipped, not under a "likely" heading
                // shared with lower-scoring runners-up.
                "Booking",
                $"{SchemaRootPickerViewModel.AllTypesHeader} (3)",
                "Address", "Airport"
            },
            Names(picker));

        // The shortlisted entry carries its score and description; the rest don't claim one.
        var booking = picker.Picks.Single(p => p.Name == "Booking");
        Assert.True(booking.HasScore);
        Assert.Equal("A booking and everything on it.", booking.Description);
        Assert.False(picker.Picks.Single(p => p.Name == "Airport").HasScore);
    }

    [Fact]
    public async Task ASubsetMatch_IsBadgedByItsFieldsPresent_NotByItsCoverage()
    {
        var settings = await BoundAsync("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "Product": {
                    "properties": { "asin": {}, "domainId": {}, "title": {}, "csv": {}, "offers": {} }
                  }
                }
              }
            }
            """);
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings,
            "asin", "domainId", "title", "csv", "offers",
            "brand", "categories", "imagesCSV", "manufacturer", "stats", "salesRanks", "lastUpdate",
            "color", "size", "model");

        // 33% coverage on a type that is entirely present reads as a bad guess, so the badge says
        // what actually qualified it.
        var product = picker.Picks.Single(p => p.Name == "Product");
        Assert.True(product.IsRecommended);
        Assert.Equal("5/5 fields", product.Score);
    }

    [Fact]
    public async Task RunnersUp_SitUnderAnOtherHeading_NotAlongsideTheWinner()
    {
        var settings = await BoundAsync("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "Booking": { "properties": { "reference": {}, "passengers": {}, "flights": {} } },
                  "BookingSummary": { "properties": { "reference": {}, "passengers": {}, "agent": {}, "office": {} } },
                  "Airport": { "properties": { "iata": {}, "name": {} } }
                }
              }
            }
            """);
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "reference", "passengers", "flights");

        // A 100% winner and a lower-scoring runner-up must not share one "likely" heading.
        Assert.Equal(
            new[]
            {
                "Booking",
                SchemaRootPickerViewModel.OtherMatchesHeader,
                "BookingSummary",
                $"{SchemaRootPickerViewModel.AllTypesHeader} (3)",
                "Airport"
            },
            Names(picker));

        Assert.True(picker.Picks[0].IsRecommended);
        Assert.False(picker.Picks.Single(p => p.Name == "BookingSummary").IsRecommended);
    }

    [Fact]
    public async Task WithNoWinner_TheCandidatesKeepThePlainHeading()
    {
        var settings = await BoundAsync("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "AaCommit": { "properties": { "booking": {}, "warnings": {} } },
                  "BbRetrieve": { "properties": { "booking": {}, "warnings": {} } }
                }
              }
            }
            """);
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "booking", "warnings");

        // Nothing was lifted out, so "other" would be answering a question nobody asked.
        Assert.Contains(SchemaRootPickerViewModel.MatchesHeader, Names(picker));
        Assert.DoesNotContain(SchemaRootPickerViewModel.OtherMatchesHeader, Names(picker));
    }

    [Fact]
    public async Task AllTypesHeader_IsRuledOffFromWhatPrecedesIt()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);
        Match(settings, "reference", "passengers", "flights");

        // Only the "all types" divider draws a rule; a match heading follows the entries it
        // describes and needs none.
        Assert.True(picker.Picks.Single(p => p.Name.StartsWith(SchemaRootPickerViewModel.AllTypesHeader, StringComparison.Ordinal)).ShowSeparator);
        Assert.DoesNotContain(picker.Picks, p => p.IsHeader && p.ShowSeparator && !p.Name.StartsWith(SchemaRootPickerViewModel.AllTypesHeader, StringComparison.Ordinal));
    }

    private const string SelfDescribingSchema = """
        {
          "title": "Keepa product response",
          "properties": { "timestamp": {}, "tokensLeft": {}, "refillIn": {}, "products": {} },
          "$defs": {
            "product": { "properties": { "asin": {}, "domainId": {}, "title": {} } },
            "offer": { "properties": { "offerId": {}, "sellerId": {} } }
          }
        }
        """;

    [Fact]
    public async Task SelfDescribingSchema_MarksWholeDocumentAsTheMatch_AndDoesNotClaimNothingWasRecognised()
    {
        var settings = await BoundAsync(SelfDescribingSchema);
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "timestamp", "tokensLeft", "refillIn", "products");

        var wholeDocument = picker.Picks[0];
        Assert.Equal(SchemaRootPickerViewModel.DocumentRootLabel, wholeDocument.Name);
        Assert.True(wholeDocument.IsRecommended);
        Assert.True(wholeDocument.HasScore);

        // The bug this covers: the answer was the entry at the top all along, but it was never
        // scored, so the picker announced that nothing had been recognised.
        Assert.DoesNotContain(SchemaRootPickerViewModel.NoMatchHeader, Names(picker));
    }

    [Fact]
    public async Task SelfDescribingSchema_StillOffersItsInnerTypes()
    {
        var settings = await BoundAsync(SelfDescribingSchema);
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "asin", "domainId", "title");

        // A document that *is* one of the inner pieces still resolves to that piece.
        var product = picker.Picks.Single(p => p.Name == "product");
        Assert.True(product.IsRecommended);
        Assert.False(picker.Picks[0].IsRecommended);
    }

    [Fact]
    public async Task OnlyOneEntryIsEverMarkedAsTheMatch()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "reference", "passengers", "flights");

        Assert.Single(picker.Picks, p => p.IsRecommended);
    }

    [Fact]
    public async Task AmbiguousMatch_MarksNothing()
    {
        var settings = await BoundAsync("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "CommitBookingResponse": { "properties": { "booking": {}, "warnings": {} } },
                  "RetrieveBookingResponse": { "properties": { "booking": {}, "warnings": {} } }
                }
              }
            }
            """);
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "booking", "warnings");

        // Both are listed as plausible; marking one would be a guess dressed as an answer.
        Assert.DoesNotContain(picker.Picks, p => p.IsRecommended);
        Assert.Contains(SchemaRootPickerViewModel.MatchesHeader, Names(picker));
    }

    [Fact]
    public async Task ADefaultedRoot_IsNotPersistedAsAChoice()
    {
        var settings = await BoundAsync();

        // Re-deriving the default each open lets the match improve; remembering it would freeze
        // a guess made before indexing had gone far enough to score anything.
        Assert.Equal("Address", settings.SelectedRootName);
        Assert.False(settings.IsRootExplicitlyChosen);

        settings.SelectRoot("Airport");
        Assert.True(settings.IsRootExplicitlyChosen);
    }

    [Fact]
    public async Task AMatchUpgradedDefault_IsStillNotAChoice()
    {
        var settings = await BoundAsync();

        Match(settings, "reference", "passengers", "flights");

        Assert.Equal("Booking", settings.SelectedRootName);
        Assert.False(settings.IsRootExplicitlyChosen);
    }

    [Fact]
    public async Task MultiRootSchema_BindsItsFirstType_RatherThanNothing()
    {
        var settings = await BoundAsync();

        // Binding the wrong type can't error, so a usable default beats an inert placeholder the
        // user has to clear before the schema does anything.
        Assert.Equal("Address", settings.SelectedRootName);
    }

    [Fact]
    public async Task ArrivingScores_UpgradeTheDefaultToTheBestMatch()
    {
        var settings = await BoundAsync();
        Assert.Equal("Address", settings.SelectedRootName);

        Match(settings, "reference", "passengers", "flights");

        Assert.Equal("Booking", settings.SelectedRootName);
    }

    [Fact]
    public async Task ArrivingScores_NeverOverrideAChoiceTheUserMade()
    {
        var settings = await BoundAsync();
        settings.SelectRoot("Airport");

        Match(settings, "reference", "passengers", "flights");

        Assert.Equal("Airport", settings.SelectedRootName);
    }

    [Fact]
    public async Task ArrivingScores_NeverOverrideARememberedChoice()
    {
        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, ApiSchema);

        var settings = new JsonSchemaSettings();
        await settings.SelectAsync(new SchemaCatalogEntry("api", path, IsUser: true), rootName: "Airport");

        Match(settings, "reference", "passengers", "flights");

        Assert.Equal("Airport", settings.SelectedRootName);
    }

    [Fact]
    public async Task AmbiguousScores_LeaveTheDefaultAlone()
    {
        var settings = await BoundAsync("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "AaCommit": { "properties": { "booking": {}, "warnings": {} } },
                  "BbRetrieve": { "properties": { "booking": {}, "warnings": {} } }
                }
              }
            }
            """);
        Assert.Equal("AaCommit", settings.SelectedRootName);

        Match(settings, "booking", "warnings");

        // Indistinguishable on names, so there is nothing better to move to.
        Assert.Equal("AaCommit", settings.SelectedRootName);
    }

    [Fact]
    public async Task SelfDescribingSchema_DefaultsToItsOwnRoot_NotItsFirstDef()
    {
        var settings = await BoundAsync(SelfDescribingSchema);

        // The schema's own root is already the right default here; it must not be pushed onto
        // "offer" just because the file also has $defs.
        Assert.Null(settings.SelectedRootName);
        Assert.Equal(SchemaRootPickerViewModel.DocumentRootLabel, new SchemaRootPickerViewModel(settings).ButtonText);
    }

    [Fact]
    public async Task NoTypeEntry_IsNotOffered()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        // "No schema" in the schema list is how you see nothing; a second way to bind nothing
        // only reads as a dead end.
        Assert.DoesNotContain(picker.Picks, p => p.Name == "No type");
        Assert.DoesNotContain(picker.Picks, p => p.Name == SchemaRootPickerViewModel.DocumentRootLabel);
    }

    [Fact]
    public async Task WhenNothingMatches_SaysSoRatherThanShowingABareList()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        Match(settings, "totally", "unrelated", "keys");

        Assert.Contains(SchemaRootPickerViewModel.NoMatchHeader, Names(picker));
        Assert.DoesNotContain(SchemaRootPickerViewModel.MatchesHeader, Names(picker));
    }

    [Fact]
    public async Task Filter_NarrowsTheList_ButKeepsTheUnbindEntryReachable()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        picker.Filter = "air";

        Assert.Equal(
            new[] { $"{SchemaRootPickerViewModel.AllTypesHeader} (3)", "Airport" },
            Names(picker));
    }

    [Fact]
    public async Task Filter_IsCaseInsensitive_AndMatchesAnywhereInTheName()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        picker.Filter = "PORT";

        Assert.Contains("Airport", Names(picker));
        Assert.DoesNotContain("Booking", Names(picker));
    }

    [Fact]
    public async Task SelectingAType_BindsIt()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        Pick(picker, "Booking");
        Assert.Equal("Booking", settings.SelectedRootName);

        Pick(picker, "Airport");
        Assert.Equal("Airport", settings.SelectedRootName);
    }

    /// <summary>
    /// Regression: binding the picked type rebuilt Picks and closed the flyout from inside the
    /// ListBox's own selection commit, leaving that commit indexing into a list that no longer
    /// existed - an unhandled ArgumentOutOfRangeException on the input path. See UiDeferral.
    /// </summary>
    [Fact]
    public async Task SelectingAType_DoesNotBindItInsideTheSelectionCommit()
    {
        var settings = await BoundAsync();
        bool closed = false;
        var picker = new SchemaRootPickerViewModel(settings, closeRequested: () => closed = true);

        picker.SelectedPick = picker.Picks.Single(p => p.Name == "Booking");

        // A multi-root schema binds something the moment it loads, so the "not yet" assertion is
        // that the *pick* hasn't landed, not that nothing is bound.
        Assert.NotEqual("Booking", settings.SelectedRootName);
        Assert.False(closed);

        ui.Pump();

        Assert.Equal("Booking", settings.SelectedRootName);
        Assert.True(closed);
    }

    [Fact]
    public async Task SelectingAHeaderOrNull_ChangesNothing()
    {
        // The ListBox nulls its selection whenever the filtered list is swapped; acting on that
        // would unbind the type the user just chose.
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);
        Pick(picker, "Booking");

        picker.SelectedPick = null;
        ui.Pump();
        Assert.Equal("Booking", settings.SelectedRootName);

        picker.SelectedPick = picker.Picks.First(p => p.IsHeader);
        ui.Pump();
        Assert.Equal("Booking", settings.SelectedRootName);
    }

    [Fact]
    public async Task ArrayMatchContext_IsStated_SoAnElementMatchIsNotMisread()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);

        settings.SetRootMatches(
            JsonSchemaRootMatcher.Rank(settings.Document!, Keys("line1", "city")),
            describeArrayElements: true);

        Assert.True(picker.HasMatchContext);
        Assert.Contains("first element", picker.MatchContextText!);
    }

    [Fact]
    public async Task UsableSchemaRoot_OffersWholeDocumentInsteadOfNoType()
    {
        var settings = await BoundAsync("""
            { "title": "Root", "properties": { "a": {} }, "$defs": { "Feature": { "title": "Feature" } } }
            """);
        var picker = new SchemaRootPickerViewModel(settings);

        Assert.Equal(SchemaRootPickerViewModel.DocumentRootLabel, picker.Picks[0].Name);
        Assert.Equal(SchemaRootPickerViewModel.DocumentRootLabel, picker.ButtonText);
    }

    [Fact]
    public async Task SwitchingSchema_DropsThePreviousSchemasScores()
    {
        var settings = await BoundAsync();
        var picker = new SchemaRootPickerViewModel(settings);
        Match(settings, "reference", "passengers", "flights");
        Assert.Contains(picker.Picks, p => p.IsRecommended);

        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string other = Path.Combine(directory, "other.json");
        File.WriteAllText(other, """
            { "openapi": "3.0.3", "components": { "schemas": { "Other": { "properties": { "z": {} } } } } }
            """);
        await settings.SelectAsync(new SchemaCatalogEntry("other", other, IsUser: true));

        // Showing the previous file's scores until a recompute lands would be a lie.
        Assert.Empty(settings.RootMatches);
        Assert.DoesNotContain(picker.Picks, p => p.IsRecommended);
    }
}
