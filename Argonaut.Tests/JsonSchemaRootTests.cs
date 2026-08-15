using System.Text;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests;

/// <summary>
/// Covers loading a schema file that holds several independently-usable schemas - chiefly an
/// OpenAPI document, whose own root is not a schema at all - and binding one of them as the root
/// the row walk starts from.
/// </summary>
public class JsonSchemaRootTests
{
    private static JsonSchemaDocument Parse(string json)
        => JsonSchemaLoader.TryParse(json) ?? throw new InvalidOperationException("Schema failed to load.");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string? MemberTitle(JsonSchemaDocument schema, int parentId, string key)
        => schema.GetTitle(schema.ResolveMember(parentId, Utf8(key)));

    /// <summary>A cut-down OpenAPI 3.0 document: schemas under components, refs between them,
    /// and a root carrying nothing the walk can use.</summary>
    private const string OpenApi = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Kite API", "version": "1.0" },
          "paths": {},
          "components": {
            "schemas": {
              "Booking": {
                "title": "Booking",
                "properties": {
                  "reference": { "title": "Booking reference" },
                  "passenger": { "$ref": "#/components/schemas/Passenger" }
                }
              },
              "Passenger": {
                "title": "Passenger",
                "properties": { "surname": { "title": "Surname" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void OpenApiComponents_BecomeNamedRoots()
    {
        var schema = Parse(OpenApi);

        Assert.Equal(new[] { "Booking", "Passenger" }, schema.NamedRoots.Select(r => r.Name));
    }

    [Fact]
    public void OpenApiDocumentRoot_IsNotUsable()
    {
        var schema = Parse(OpenApi);

        // openapi/info/paths/components are not schema keywords, so binding the file's own root
        // would label nothing - which is what tells the UI to require a type choice.
        Assert.False(schema.DocumentRootIsUsable);
        Assert.Null(MemberTitle(schema, schema.RootId, "info"));
    }

    [Fact]
    public void WithRoot_BindsTheNamedComponent()
    {
        var schema = Parse(OpenApi).WithRoot("Booking");

        Assert.Equal("Booking", schema.RootName);
        Assert.Equal("Booking", schema.GetTitle(schema.RootId));
        Assert.Equal("Booking reference", MemberTitle(schema, schema.RootId, "reference"));
    }

    [Fact]
    public void ComponentRefs_ResolveBetweenNamedRoots()
    {
        var schema = Parse(OpenApi).WithRoot("Booking");

        int passenger = schema.ResolveMember(schema.RootId, Utf8("passenger"));

        Assert.Equal("Passenger", schema.GetTitle(passenger));
        Assert.Equal("Surname", MemberTitle(schema, passenger, "surname"));
    }

    [Fact]
    public void WithRoot_Null_ReturnsToTheDocumentRoot()
    {
        var schema = Parse(OpenApi).WithRoot("Booking").WithRoot(null);

        Assert.Null(schema.RootName);
        Assert.Equal(schema.DocumentRootId, schema.RootId);
    }

    [Fact]
    public void WithRoot_UnknownName_FallsBackToTheDocumentRoot()
    {
        // The schema file may have been edited since the name was remembered; that must not
        // leave the document bound to a node that no longer exists.
        var schema = Parse(OpenApi).WithRoot("Deleted");

        Assert.Null(schema.RootName);
        Assert.Equal(schema.DocumentRootId, schema.RootId);
    }

    [Fact]
    public void WithRoot_NoOp_ReturnsTheSameInstance()
    {
        var schema = Parse(OpenApi);

        // JsonSchemaSettings.SetDocument short-circuits on reference equality, so a re-selection
        // of the already-bound root must not look like a change and rebuild every visible row.
        Assert.Same(schema, schema.WithRoot(null));
        var bound = schema.WithRoot("Booking");
        Assert.Same(bound, bound.WithRoot("Booking"));
    }

    [Fact]
    public void Swagger2Definitions_BecomeNamedRoots()
    {
        // Swagger 2.0 needs no special handling - its schemas are already under a root
        // `definitions`, which the loader has always read.
        var schema = Parse("""
            {
              "swagger": "2.0",
              "definitions": {
                "Pet": { "title": "Pet", "properties": { "name": { "title": "Pet name" } } }
              }
            }
            """);

        Assert.Equal(new[] { "Pet" }, schema.NamedRoots.Select(r => r.Name));
        Assert.Equal("Pet name", MemberTitle(schema.WithRoot("Pet"), schema.WithRoot("Pet").RootId, "name"));
    }

    [Fact]
    public void OrdinarySchemaWithDefs_KeepsAUsableDocumentRoot()
    {
        var schema = Parse("""
            {
              "title": "Feature collection",
              "properties": { "features": { "items": { "$ref": "#/$defs/Feature" } } },
              "$defs": { "Feature": { "title": "Feature" } }
            }
            """);

        // The defs are still offered as roots (binding a file that is a bare Feature is useful),
        // but the document root remains the default because it says something on its own.
        Assert.True(schema.DocumentRootIsUsable);
        Assert.Equal(new[] { "Feature" }, schema.NamedRoots.Select(r => r.Name));
        Assert.Null(schema.RootName);
    }

    [Fact]
    public void SingleSchemaFile_OffersNoNamedRoots()
    {
        var schema = Parse("""{ "title": "Thing", "properties": { "a": { "title": "A" } } }""");

        Assert.Empty(schema.NamedRoots);
        Assert.True(schema.DocumentRootIsUsable);
    }

    [Fact]
    public void EmptyDefinitions_AreNotOfferedAsRoots()
    {
        var schema = Parse("""
            {
              "title": "Thing",
              "$defs": { "Useful": { "title": "Useful" }, "Empty": {} }
            }
            """);

        Assert.Equal(new[] { "Useful" }, schema.NamedRoots.Select(r => r.Name));
    }

    [Fact]
    public void NamedRoots_AreSortedByName()
    {
        var schema = Parse("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "Zebra": { "title": "Zebra" },
                  "apple": { "title": "apple" },
                  "Mango": { "title": "Mango" }
                }
              }
            }
            """);

        Assert.Equal(new[] { "apple", "Mango", "Zebra" }, schema.NamedRoots.Select(r => r.Name));
    }
}
