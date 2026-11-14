using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// One document's side of "Open matched schemas" and the fit-ranked schema list, shared by the
/// JSON and NDJSON view models, which differ only in where a document's property names come from.
///
/// Measures the catalog against the document's sampled names: ranks the schema list
/// (<see cref="JsonSchemaSettings.Fits"/>) and, when allowed, binds the schema that clearly fits.
/// Silent until the catalog has been gathered, so the owner can call it from every point where
/// either side may have arrived. UI thread only, like the settings it drives.
/// </summary>
/// <param name="matcher">Measures the catalog; shared across documents.</param>
/// <param name="schemaSettings">The document's schema state, which this ranks and may bind.</param>
/// <param name="openMatchedSchemas">Reads the option at the moment of binding, so turning it off
/// stops a measurement already running from binding.</param>
public sealed class SchemaCatalogMeasurement(ISchemaMatcher matcher, JsonSchemaSettings schemaSettings, Func<bool> openMatchedSchemas) : IDisposable
{
    // Fires when the document closes, ending a measurement still running for it.
    private readonly CancellationTokenSource documentClosing = new();

    private bool catalogGathered;

    // The user's last choice for this file was "No schema", so nothing is matched to it.
    private bool choseNoSchema;

    /// <summary>The catalog has been listed for this document: from here on it can be measured.
    /// Call after any remembered binding has been selected, so a match cannot race it.</summary>
    public void CatalogGathered(IReadOnlyList<SchemaBinding> bindings, string? documentPath)
    {
        choseNoSchema = SchemaBindings.ChoseNoSchema(bindings, documentPath);
        catalogGathered = true;
    }

    /// <summary>
    /// Ranks the schema list against the names <paramref name="sampleKeys"/> reads from the
    /// document, and binds the clear fit when <paramref name="mayBind"/>, the option is on, the
    /// user did not choose "No schema" for this file and nothing has been chosen this session.
    /// <paramref name="sampleKeys"/> runs on the calling thread and returns empty when there is
    /// nothing to read yet, or nothing that should be measured.
    /// </summary>
    public async Task MeasureAsync(Func<IReadOnlyList<byte[]>> sampleKeys, bool mayBind)
    {
        if (!catalogGathered || documentClosing.IsCancellationRequested)
            return;

        var entries = schemaSettings.Entries;
        if (entries.Count == 0)
            return;

        var keys = sampleKeys();
        if (keys.Count == 0)
            return;

        SchemaCatalogFit fit;
        try
        {
            fit = await matcher.FitAsync(entries, keys, documentClosing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A refresh that replaced the catalog meanwhile measures again itself.
        if (documentClosing.IsCancellationRequested || !ReferenceEquals(entries, schemaSettings.Entries))
            return;

        schemaSettings.SetFits(fit.Fits, fit.AutoMatch?.Entry);

        if (mayBind && fit.AutoMatch is { } match && !choseNoSchema && openMatchedSchemas())
            await schemaSettings.SelectMatchedAsync(match.Entry, match.RootName);
    }

    public void Dispose()
    {
        documentClosing.Cancel();
        documentClosing.Dispose();
    }
}
