using System.IO;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// Seeds the user's schema folder with a worked example the first time they open it, so the folder
/// is never empty and never needs a docs page to get going.
///
/// The example is not a separate artefact: it is the shipped GeoJSON schema
/// (<c>Argonaut/Schemas/geojson.json</c>), which is a fully usable schema in its own right - it
/// appears in the dropdown as "geojson" and labels any GeoJSON document - and carries its
/// authoring guidance in <c>$comment</c>. One file serves both purposes, so the guidance can never
/// drift out of step with a schema that actually works.
///
/// The copy lands under <see cref="UserCopyFileName"/> rather than its own name. That suffix is
/// filtered out of the catalog (see <see cref="JsonSchemaCatalog.ExampleSuffix"/>), which is what
/// stops the user's copy appearing in the dropdown a second time next to the bundled original.
/// The copy has to live in the user folder because the install directory isn't writable - editing
/// is the whole point of handing someone an example.
///
/// GeoJSON (RFC 7946) is the subject because that one format naturally contains all three array
/// shapes the display rules distinguish - a fixed-layout tuple (<c>bbox</c>), a long array of
/// identical objects (<c>features</c>), and an array whose shape varies by a sibling's value
/// (<c>coordinates</c>) - so every piece of guidance sits on a real structure rather than an
/// invented one.
/// </summary>
internal static class JsonSchemaExample
{
    /// <summary>The shipped schema that is copied out. A normal catalog entry, listed as "geojson".</summary>
    public const string BundledFileName = "geojson.json";

    /// <summary>Name the copy takes in the user's folder - suffixed so the catalog skips it.</summary>
    public const string UserCopyFileName = "geojson" + JsonSchemaCatalog.ExampleSuffix;

    /// <summary>
    /// Copies the shipped schema into <paramref name="directory"/> as the example, unless it's
    /// already there - an edited copy is never clobbered. Deleting it does bring it back on the
    /// next open, which is the point: the folder should always offer a worked example to crib from.
    ///
    /// Best-effort, like everything else that touches the settings folder: an unwritable folder or
    /// an install missing the shipped file must never stop the folder from opening.
    /// </summary>
    public static void TryCopyTo(string directory)
    {
        try
        {
            string destination = Path.Combine(directory, UserCopyFileName);
            if (File.Exists(destination))
                return;

            string source = Path.Combine(JsonSchemaCatalog.GetBundledDirectory(), BundledFileName);
            if (File.Exists(source))
                File.Copy(source, destination);
        }
        catch
        {
            // The user still gets the folder, just not the example.
        }
    }
}
