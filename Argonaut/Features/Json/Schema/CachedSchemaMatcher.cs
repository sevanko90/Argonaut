using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Features.Json.Schema;

/// <summary>
/// <see cref="ISchemaMatcher"/> over the catalog's files, keeping each schema's
/// <see cref="SchemaFingerprint"/> for the life of the app so the catalog is parsed once - on the
/// first document opened - rather than once per document.
///
/// A fingerprint is kept by path and replaced when the file's last-write time or length changes,
/// which a stat per schema per document detects. A schema that fails to load is remembered as
/// failing on the same terms, so a broken file in the user's folder is not re-parsed on every
/// open either. Only fingerprints are kept, never the parsed documents: holding the whole catalog
/// parsed would cost what binding every schema at once costs.
/// </summary>
/// <param name="load">Parses one schema file, null when it cannot be used -
/// <see cref="JsonSchemaLoader.TryLoadFile"/> in the app; tests count calls through it.</param>
public sealed class CachedSchemaMatcher(Func<string, JsonSchemaDocument?> load) : ISchemaMatcher
{
    private readonly ConcurrentDictionary<string, Kept> kept = new(StringComparer.Ordinal);

    public CachedSchemaMatcher() : this(JsonSchemaLoader.TryLoadFile)
    {
    }

    /// <summary>A fingerprint, or null for a file that did not load, as of one version of it.</summary>
    private sealed record Kept(DateTime LastWriteUtc, long Length, SchemaFingerprint? Fingerprint);

    public Task<SchemaCatalogFit> FitAsync(IReadOnlyList<SchemaCatalogEntry> entries, IReadOnlyList<byte[]> documentKeys, CancellationToken documentClosing)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(documentKeys);

        return Task.Run(() => Fit(entries, documentKeys, documentClosing), documentClosing);
    }

    private SchemaCatalogFit Fit(IReadOnlyList<SchemaCatalogEntry> entries, IReadOnlyList<byte[]> documentKeys, CancellationToken documentClosing)
    {
        if (documentKeys.Count == 0 || entries.Count == 0)
            return SchemaCatalogFit.None;

        var fingerprints = new List<SchemaFingerprint>(entries.Count);
        var owners = new List<SchemaCatalogEntry>(entries.Count);

        foreach (var entry in entries)
        {
            documentClosing.ThrowIfCancellationRequested();

            if (FingerprintOf(entry.FilePath) is { } fingerprint)
            {
                fingerprints.Add(fingerprint);
                owners.Add(entry);
            }
        }

        var (leaders, chosen) = SchemaAutoMatchRule.Assess(fingerprints, documentKeys);

        var fits = new List<SchemaFit>();
        for (int i = 0; i < leaders.Length; i++)
        {
            if (leaders[i] is { } leader && JsonSchemaRootMatcher.IsPlausible(leader))
                fits.Add(new SchemaFit(owners[i], leader));
        }

        // Plausible is all of them, so coverage then precision - as types within a schema are
        // ordered - with the name settling a tie so the list does not shuffle between opens.
        fits.Sort(static (a, b) =>
            SchemaAutoMatchRule.Before(a.Match, b.Match) ? -1
            : SchemaAutoMatchRule.Before(b.Match, a.Match) ? 1
            : string.Compare(a.Entry.DisplayName, b.Entry.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (chosen is not { } c)
            return new SchemaCatalogFit(fits, null);

        // The bound schema leads even where a schema that only plausibly fits scores higher on
        // coverage: the one bound should be the first one the list shows.
        var autoMatch = new SchemaFit(owners[c.SchemaIndex], c.Match);
        fits.Remove(autoMatch);
        fits.Insert(0, autoMatch);
        return new SchemaCatalogFit(fits, autoMatch);
    }

    private SchemaFingerprint? FingerprintOf(string path)
    {
        DateTime lastWrite;
        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            lastWrite = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch
        {
            return null;
        }

        if (kept.TryGetValue(path, out var known) && known.LastWriteUtc == lastWrite && known.Length == length)
            return known.Fingerprint;

        var fingerprint = load(path) is { } schema ? SchemaFingerprint.Of(schema) : null;
        kept[path] = new Kept(lastWrite, length, fingerprint);
        return fingerprint;
    }
}
