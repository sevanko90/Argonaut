using System;
using System.Text.Json;
using Argonaut.Engine.Text;
using Argonaut.Features.Json.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A chip for a string that holds a JSON document of its own - a serialised payload in a log or
/// an event: "JSON object · 4 members". Recognised by its start - <c>{</c> then a quote or
/// <c>}</c>, or <c>[</c> then the start of a value - and, when the whole value is within the
/// display cap, unescaped and read to the end, so a string that only starts like JSON gets
/// nothing. A longer one cannot be read in full here, so it says only that it looks like JSON,
/// and how big it is.
/// </summary>
public sealed class EmbeddedJsonHintProvider : IValueHintProvider
{
    public bool IsActive => true;

    public bool ReadsPrefixes => true;

    public bool TryClassify(JsonTokenKind kind, ReadOnlySpan<byte> rawValue, long valueLength, out ValueHintCandidate candidate)
    {
        candidate = default;
        if (kind != JsonTokenKind.String || !StartsLikeJson(rawValue))
            return false;

        candidate = new ValueHintCandidate(ValueHintKind.EmbeddedJson, valueLength, 0);
        return true;
    }

    public ValueHint? FormatHint(in ValueHintCandidate candidate, ReadOnlySpan<byte> rawValue, long valueLength, long valueOffset)
    {
        if (valueLength > rawValue.Length)
            return new ValueHint($"looks like JSON · {ByteLengthText.Format(valueLength)}", TreeRunIcon.Json);

        if (Unescape(rawValue) is not { } document || Shape(document) is not { } shape)
            return null;

        string text = shape.IsObject
            ? $"JSON object · {shape.Children} {(shape.Children == 1 ? "member" : "members")}"
            : $"JSON array · {shape.Children} {(shape.Children == 1 ? "item" : "items")}";
        return new ValueHint(text, TreeRunIcon.Json);
    }

    /// <summary>An object opening onto a member name or its close, or an array onto a value or
    /// its close - in the escaped form a JSON string holds them, quotes as <c>\"</c>.</summary>
    private static bool StartsLikeJson(ReadOnlySpan<byte> text)
    {
        text = text.TrimStart(" \t"u8);
        if (text.Length < 2 || text[0] is not ((byte)'{' or (byte)'['))
            return false;

        bool isObject = text[0] == '{';
        var rest = text[1..].TrimStart(" \t"u8);
        if (rest.IsEmpty)
            return false;
        if (rest.StartsWith("\\\""u8) || rest.StartsWith("\\n"u8) || rest.StartsWith("\\r"u8) || rest.StartsWith("\\t"u8))
            return true;
        return isObject
            ? rest[0] == '}'
            : rest[0] is (byte)']' or (byte)'{' or (byte)'[' or (byte)'-' or (byte)'t' or (byte)'f' or (byte)'n' or >= (byte)'0' and <= (byte)'9';
    }

    /// <summary>The string's content with its escapes undone, by reading it back as a JSON string.</summary>
    private static byte[]? Unescape(ReadOnlySpan<byte> rawValue)
    {
        var quoted = new byte[rawValue.Length + 2];
        quoted[0] = quoted[^1] = (byte)'"';
        rawValue.CopyTo(quoted.AsSpan(1));
        try
        {
            var reader = new Utf8JsonReader(quoted);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                return null;

            var content = new byte[rawValue.Length];
            int written = reader.CopyString(content);
            return content[..written];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the document is one whole object or array, and how many members or items
    /// it has at the top; null when it is not valid JSON.</summary>
    private static (bool IsObject, int Children)? Shape(byte[] document)
    {
        try
        {
            var reader = new Utf8JsonReader(document);
            if (!reader.Read() || reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                return null;

            bool isObject = reader.TokenType == JsonTokenType.StartObject;
            int children = 0;
            while (reader.Read() && reader.CurrentDepth > 0)
            {
                if (reader.CurrentDepth != 1)
                    continue;
                if (isObject ? reader.TokenType == JsonTokenType.PropertyName
                        : reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
                    children++;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
            }

            return reader.Read() ? null : (isObject, children);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a string holds does not depend on any setting.</summary>
    public event EventHandler? HintsChanged
    {
        add { }
        remove { }
    }
}
