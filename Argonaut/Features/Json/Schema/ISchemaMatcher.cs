using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Json.Schema;

/// <summary>How well one catalog schema fits a document: the entry, and the leading type within
/// it (<see cref="RootName"/>, null for the schema's own root) with its score.</summary>
public readonly record struct SchemaFit(SchemaCatalogEntry Entry, SchemaRootMatch Match)
{
    public string? RootName => Match.Name;
}

/// <summary>
/// The catalog measured against one document.
/// </summary>
/// <param name="Fits">The schemas that plausibly fit (<see cref="JsonSchemaRootMatcher.IsPlausible"/>),
/// best first - what leads the schema list. Schemas not listed fit nothing worth saying.</param>
/// <param name="AutoMatch">The schema to bind unasked, or null when none clears
/// <see cref="SchemaAutoMatchRule"/>'s bar or the fit is ambiguous. When set, it leads
/// <see cref="Fits"/>.</param>
public sealed record SchemaCatalogFit(IReadOnlyList<SchemaFit> Fits, SchemaFit? AutoMatch)
{
    public static SchemaCatalogFit None { get; } = new([], null);
}

/// <summary>
/// Measures the catalog's schemas against a document - ranking them for the schema list, and
/// choosing one to bind by <see cref="SchemaAutoMatchRule"/> for "Open matched schemas".
/// </summary>
public interface ISchemaMatcher
{
    /// <summary>
    /// <paramref name="entries"/> measured against a document whose outermost object carries
    /// <paramref name="documentKeys"/>. Reads and parses schema files off the calling thread; safe
    /// to call from the UI thread and from several documents at once. Throws
    /// <see cref="System.OperationCanceledException"/> once <paramref name="documentClosing"/>
    /// fires.
    /// </summary>
    Task<SchemaCatalogFit> FitAsync(IReadOnlyList<SchemaCatalogEntry> entries, IReadOnlyList<byte[]> documentKeys, CancellationToken documentClosing);
}
