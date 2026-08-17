using System.Text;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests;

/// <summary>
/// Covers ranking a schema's named roots against the keys a document actually carries, and - just
/// as important - the cases where the honest answer is "no idea", since a confidently-wrong type
/// label is worse than none.
/// </summary>
public class JsonSchemaRootMatcherTests
{
    private static JsonSchemaDocument Parse(string json)
        => JsonSchemaLoader.TryParse(json) ?? throw new InvalidOperationException("Schema failed to load.");

    private static IReadOnlyList<byte[]> Keys(params string[] names)
        => names.Select(Encoding.UTF8.GetBytes).ToArray();

    /// <summary>Three types sharing an envelope, plus one unrelated - the real shape of the
    /// problem in an API schema.</summary>
    private const string Api = """
        {
          "openapi": "3.0.3",
          "components": {
            "schemas": {
              "Booking": {
                "properties": {
                  "reference": {}, "passengers": {}, "flights": {}, "total": {}
                }
              },
              "BookingEnvelope": {
                "properties": {
                  "reference": {}, "passengers": {}, "flights": {}, "total": {},
                  "warnings": {}, "meta": {}, "traceId": {}, "elapsed": {}
                }
              },
              "Address": {
                "properties": { "line1": {}, "city": {}, "postcode": {} }
              }
            }
          }
        }
        """;

    [Fact]
    public void Rank_PutsTheTightestMatchFirst()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("reference", "passengers", "flights", "total"));

        // Booking and BookingEnvelope both cover the document fully; Booking wins on precision
        // because the document uses all of it, while half of the envelope is absent.
        Assert.Equal("Booking", ranked[0].Name);
        Assert.Equal("BookingEnvelope", ranked[1].Name);
        Assert.Equal(1.0, ranked[0].Coverage);
        Assert.Equal(1.0, ranked[0].Precision);
        Assert.Equal(0.5, ranked[1].Precision);
    }

    [Fact]
    public void Rank_IncludesEveryRoot_EvenUnmatchedOnes()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("reference", "passengers", "flights", "total"));

        Assert.Equal(3, ranked.Count);
        Assert.Equal(0, ranked[^1].MatchedKeys);
    }

    [Fact]
    public void Best_PicksTheClearWinner()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("line1", "city", "postcode"));

        Assert.Equal("Address", JsonSchemaRootMatcher.Best(ranked)!.Value.Name);
    }

    [Fact]
    public void Best_DeclinesWhenTheTopTwoAreIndistinguishable()
    {
        var schema = Parse("""
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

        var ranked = JsonSchemaRootMatcher.Rank(schema, Keys("booking", "warnings"));

        // Identical on names, so names cannot separate them - the user has to choose.
        Assert.Null(JsonSchemaRootMatcher.Best(ranked));
    }

    [Fact]
    public void Best_DeclinesOnASingleSharedKey()
    {
        // `id` alone matches dozens of unrelated types; one key is not evidence.
        var schema = Parse("""
            {
              "openapi": "3.0.3",
              "components": { "schemas": { "Thing": { "properties": { "id": {} } } } }
            }
            """);

        var ranked = JsonSchemaRootMatcher.Rank(schema, Keys("id"));

        Assert.Equal(1.0, ranked[0].Coverage);
        Assert.Null(JsonSchemaRootMatcher.Best(ranked));
    }

    [Fact]
    public void Best_DeclinesForAWrapperRootThatMatchesNothing()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("data", "meta"));

        Assert.Null(JsonSchemaRootMatcher.Best(ranked));
    }

    [Fact]
    public void Rank_IsUnaffectedByKeyOrderOrDuplicates()
    {
        var ordered = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("line1", "city", "postcode"));
        var jumbled = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("postcode", "city", "line1", "city"));

        Assert.Equal(ordered[0].Name, jumbled[0].Name);
        Assert.Equal(ordered[0].Coverage, jumbled[0].Coverage);
        Assert.Equal(ordered[0].MatchedKeys, jumbled[0].MatchedKeys);
    }

    [Fact]
    public void Rank_WithNoDocumentKeys_ScoresNothing()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Array.Empty<byte[]>());

        Assert.All(ranked, m => Assert.Equal(0, m.MatchedKeys));
        Assert.Null(JsonSchemaRootMatcher.Best(ranked));
    }

    /// <summary>A schema that discriminates internally: its root describes the whole file and its
    /// $defs hold only the inner pieces. The shape of the shipped Keepa schema.</summary>
    private const string SelfDescribing = """
        {
          "title": "Keepa product response",
          "properties": {
            "timestamp": {}, "tokensLeft": {}, "refillIn": {}, "refillRate": {},
            "tokensConsumed": {}, "processingTimeInMs": {}, "products": {}
          },
          "$defs": {
            "product": { "properties": { "asin": {}, "domainId": {}, "title": {} } },
            "offer": { "properties": { "offerId": {}, "sellerId": {}, "isPrime": {} } }
          }
        }
        """;

    [Fact]
    public void Rank_ScoresTheSchemasOwnRoot_NotOnlyTheNamedOnes()
    {
        var ranked = JsonSchemaRootMatcher.Rank(
            Parse(SelfDescribing),
            Keys("timestamp", "tokensLeft", "refillIn", "refillRate", "tokensConsumed", "processingTimeInMs", "products"));

        // Without the document root as a candidate this reports "nothing recognised" for exactly
        // the schemas that need no type picked at all.
        Assert.Null(ranked[0].Name);
        Assert.Equal(1.0, ranked[0].Coverage);
        Assert.Equal(1.0, ranked[0].Precision);
    }

    [Fact]
    public void Best_RecommendsTheSchemasOwnRoot_ForASelfDescribingSchema()
    {
        var ranked = JsonSchemaRootMatcher.Rank(
            Parse(SelfDescribing),
            Keys("timestamp", "tokensLeft", "refillIn", "products"));

        var best = JsonSchemaRootMatcher.Best(ranked);

        Assert.NotNull(best);
        Assert.Null(best!.Value.Name); // null name = "the whole document"
    }

    [Fact]
    public void Rank_StillPrefersANamedRoot_WhenTheDocumentIsOneOfTheInnerPieces()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(SelfDescribing), Keys("asin", "domainId", "title"));

        Assert.Equal("product", JsonSchemaRootMatcher.Best(ranked)!.Value.Name);
    }

    [Fact]
    public void Rank_OmitsAnUnusableDocumentRoot()
    {
        // An OpenAPI root carries openapi/info/paths and no schema keywords, so it is not a
        // candidate and must not dilute the ranking.
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("line1", "city", "postcode"));

        Assert.All(ranked, m => Assert.NotNull(m.Name));
    }

    [Fact]
    public void DocumentRoot_WinsAnOutrightTieWithANamedRoot()
    {
        // Root and def describe the same shape: the user shouldn't have to pick a type at all.
        var schema = Parse("""
            {
              "title": "Thing",
              "properties": { "a": {}, "b": {} },
              "$defs": { "Thing": { "properties": { "a": {}, "b": {} } } }
            }
            """);

        var ranked = JsonSchemaRootMatcher.Rank(schema, Keys("a", "b"));

        Assert.Null(ranked[0].Name);
    }

    [Fact]
    public void Rank_OnASchemaWithoutNamedRoots_IsEmpty()
    {
        var schema = Parse("""{ "title": "Thing", "properties": { "a": {} } }""");

        Assert.Empty(JsonSchemaRootMatcher.Rank(schema, Keys("a")));
    }

    /// <summary>
    /// The shipped Keepa schema's real proportions: a root describing the API envelope, and a
    /// `product` def that documents a handful of the ~100 fields a live product object carries.
    /// `catalogue` is the noise a partial schema has to beat - it shares more of the document's
    /// keys than `product` does while accounting for almost none of itself.
    /// </summary>
    private const string PartialSchema = """
        {
          "title": "Product response",
          "properties": { "timestamp": {}, "tokensLeft": {}, "products": {} },
          "$defs": {
            "product": {
              "properties": { "asin": {}, "domainId": {}, "title": {}, "csv": {}, "offers": {} }
            },
            "catalogue": {
              "properties": {
                "brand": {}, "categories": {}, "imagesCSV": {}, "manufacturer": {}, "stats": {},
                "salesRanks": {}, "lastUpdate": {}, "color": {}, "size": {},
                "c1": {}, "c2": {}, "c3": {}, "c4": {}, "c5": {}, "c6": {}, "c7": {}, "c8": {},
                "c9": {}, "c10": {}, "c11": {}, "c12": {}, "c13": {}, "c14": {}, "c15": {},
                "c16": {}, "c17": {}, "c18": {}, "c19": {}, "c20": {}, "c21": {}
              }
            }
          }
        }
        """;

    /// <summary>A live product object: everything `product` declares, plus much more besides.</summary>
    private static IReadOnlyList<byte[]> LiveProductKeys() => Keys(
        "asin", "domainId", "title", "csv", "offers",
        "brand", "categories", "imagesCSV", "manufacturer", "stats", "salesRanks", "lastUpdate",
        "color", "size", "model", "partNumber", "packageHeight", "packageWeight", "eanList", "upcList");

    [Fact]
    public void Best_RecommendsAPartialSchemaThatTheDocumentFullyContains()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(PartialSchema), LiveProductKeys());

        // 5 of 20 document keys is 25% coverage - far under the coverage floor - but every field
        // the type declares is present. Rejecting this leaves the user picking `product` by hand
        // on every such document, which is the whole complaint.
        var best = JsonSchemaRootMatcher.Best(ranked);
        Assert.Equal("product", best!.Value.Name);
        Assert.Equal(1.0, best.Value.Precision);
        Assert.True(best.Value.Coverage < JsonSchemaRootMatcher.MinimumCoverage);
        Assert.True(JsonSchemaRootMatcher.IsSubsetMatch(best.Value));
    }

    [Fact]
    public void Rank_PutsAFullyPresentSubsetAboveHigherCoverageNoise()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(PartialSchema), LiveProductKeys());

        // `catalogue` shares 9 keys to `product`'s 5, so coverage alone would order it first and
        // leave Best reading a candidate that qualifies on neither measure.
        Assert.Equal("product", ranked[0].Name);
        Assert.Equal("catalogue", ranked[1].Name);
        Assert.True(ranked[1].Coverage > ranked[0].Coverage);
        Assert.False(JsonSchemaRootMatcher.IsPlausible(ranked[1]));
    }

    [Fact]
    public void Best_StillRecommendsTheEnvelopeForTheWholeResponse()
    {
        // The other half of the same schema: the container document must keep matching the root,
        // not get pulled onto a def by the new path.
        var ranked = JsonSchemaRootMatcher.Rank(Parse(PartialSchema), Keys("timestamp", "tokensLeft", "products"));

        Assert.Null(JsonSchemaRootMatcher.Best(ranked)!.Value.Name);
    }

    [Fact]
    public void Best_DeclinesForASmallTypeFullyPresentInABigDocument()
    {
        // Precision on its own is what MinimumPreciseMatchedKeys guards: three common names
        // landing in a twenty-key document is coincidence, not identification.
        var schema = Parse("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": { "Stamp": { "properties": { "asin": {}, "title": {}, "lastUpdate": {} } } }
              }
            }
            """);

        var ranked = JsonSchemaRootMatcher.Rank(schema, LiveProductKeys());

        Assert.Equal(1.0, ranked[0].Precision);
        Assert.Null(JsonSchemaRootMatcher.Best(ranked));
    }

    [Fact]
    public void Best_DeclinesBetweenTwoEquallyContainedSubsets()
    {
        // Two partial types both fully present: the new path must decline the same way the
        // coverage path does rather than picking whichever sorted first.
        var schema = Parse("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "ProductA": { "properties": { "asin": {}, "domainId": {}, "title": {}, "csv": {}, "offers": {} } },
                  "ProductB": { "properties": { "asin": {}, "domainId": {}, "title": {}, "csv": {}, "stats": {} } }
                }
              }
            }
            """);

        var ranked = JsonSchemaRootMatcher.Rank(schema, LiveProductKeys());

        Assert.Null(JsonSchemaRootMatcher.Best(ranked));
    }

    [Fact]
    public void IsSubsetMatch_IsFalseForATypeThatExplainsTheWholeDocument()
    {
        var ranked = JsonSchemaRootMatcher.Rank(Parse(Api), Keys("line1", "city", "postcode"));

        Assert.True(JsonSchemaRootMatcher.IsPlausible(ranked[0]));
        Assert.False(JsonSchemaRootMatcher.IsSubsetMatch(ranked[0]));
    }

    [Fact]
    public void RealWorldShape_PrefersThePayloadOverTheEnvelope()
    {
        // The case that motivates precision as a tiebreak: a document that is the payload should
        // not be labelled as the envelope that can also contain it.
        var ranked = JsonSchemaRootMatcher.Rank(
            Parse(Api),
            Keys("reference", "passengers", "flights"));

        Assert.Equal("Booking", JsonSchemaRootMatcher.Best(ranked)!.Value.Name);
    }
}
