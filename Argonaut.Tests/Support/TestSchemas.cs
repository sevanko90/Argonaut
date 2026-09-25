using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Support;

/// <summary>Schema catalogs for tests, so none of them reads or writes the developer's real
/// schema folder or opens a file manager.</summary>
internal static class TestSchemas
{
    /// <summary>The bundled schemas, and a user folder that does not exist. For tests that need a
    /// catalog to construct something but never add a schema of their own.</summary>
    public static JsonSchemaCatalog Catalog() => new(
        JsonSchemaCatalog.BundledDirectoryBesideApp,
        Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"), "Schemas"),
        revealDirectory: _ => { });
}
