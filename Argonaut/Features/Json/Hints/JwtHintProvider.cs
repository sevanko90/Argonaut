using System;
using System.Buffers.Text;
using System.Text.Json;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A chip for a JSON Web Token: its algorithm, and when it expires or expired. Recognised by
/// shape - three base64url parts, the first starting <c>eyJ</c>, which is how every JSON object
/// header encodes - and confirmed by decoding: a header that is not a JSON object naming an
/// <c>alg</c> is not a token. Only the header and claims are read; the signature is never checked,
/// since a viewer has no key to check it with.
/// </summary>
public sealed class JwtHintProvider(TimeProvider clock) : IValueHintProvider
{
    public bool IsActive => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || rawValue.Length < 10 || !rawValue.StartsWith("eyJ"u8))
            return false;

        int dots = 0;
        foreach (byte b in rawValue)
        {
            if (b == '.')
                dots++;
            else if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_'))
                return false;
        }

        if (dots != 2)
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.Jwt, 0, 0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueOffset)
    {
        int first = rawValue.IndexOf((byte)'.');
        int second = first + 1 + rawValue[(first + 1)..].IndexOf((byte)'.');
        if (ReadString(rawValue[..first], "alg"u8) is not { } algorithm)
            return null;

        string text = $"JWT {algorithm}";
        if (ReadNumber(rawValue[(first + 1)..second], "exp"u8) is { } expires)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(expires, -62135596800, 253402300799));
            string when = RelativeTime.Describe(at - clock.GetUtcNow());
            text += at > clock.GetUtcNow() ? $" · expires {when}" : $" · expired {when}";
        }

        return new ValueHint(text, TreeRunIcon.Key);
    }

    /// <summary>A string member of the base64url-encoded JSON object <paramref name="part"/>, or
    /// null when the part does not decode to an object holding one.</summary>
    private static string? ReadString(ReadOnlySpan<byte> part, ReadOnlySpan<byte> name)
    {
        var reader = Reader(part, name, out bool found);
        return found && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
    }

    private static long? ReadNumber(ReadOnlySpan<byte> part, ReadOnlySpan<byte> name)
    {
        var reader = Reader(part, name, out bool found);
        return found && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long number) ? number : null;
    }

    /// <summary>A reader over the decoded part, on the value of its top-level member
    /// <paramref name="name"/> when <paramref name="found"/>.</summary>
    private static Utf8JsonReader Reader(ReadOnlySpan<byte> part, ReadOnlySpan<byte> name, out bool found)
    {
        found = false;
        byte[] json;
        try
        {
            json = Base64Url.DecodeFromUtf8(part);
        }
        catch (FormatException)
        {
            return default;
        }

        var reader = new Utf8JsonReader(json);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return default;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isWanted = reader.ValueTextEquals(name);
                if (!reader.Read())
                    return default;
                if (isWanted)
                {
                    found = true;
                    return reader;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return default;
    }

    /// <summary>A token's text does not depend on any setting; its expiry reading refreshes when
    /// the row is drawn again.</summary>
    public event EventHandler? HintsChanged
    {
        add { }
        remove { }
    }
}
