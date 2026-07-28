using System.Collections.Generic;

namespace Argonaut.Infrastructure;

/// <summary>Shared "where did it stop" text for an <see cref="IndexFailure"/>, used by both
/// the incompatible-file placeholder and the shell's partial-failure banner.</summary>
public static class IndexFailureFormatting
{
    /// <summary>
    /// "Line N, column M (byte K)"-style summary of where a scan stopped, omitting any parts
    /// the indexer couldn't supply; empty when there's no location info at all (e.g. a
    /// pre-flight rejection, which never gets past the header check to a line/column).
    /// </summary>
    public static string DescribeLocation(IndexFailure failure)
    {
        var parts = new List<string>();
        if (failure.Line is { } line)
            parts.Add(failure.Column is { } col ? $"Line {line:N0}, column {col:N0}" : $"Line {line:N0}");
        if (failure.ByteOffset is { } offset)
            parts.Add($"byte {offset:N0}");

        return parts.Count == 0 ? string.Empty : string.Join(" (", parts) + (parts.Count > 1 ? ")" : "");
    }
}
