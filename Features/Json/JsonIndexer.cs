using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using JsonViewerCore.Infrastructure;

namespace JsonViewerCore.Features.Json;

public enum JsonTokenKind
{
    StartObject,
    EndObject,
    StartArray,
    EndArray,
    PropertyName,
    String,
    Number,
    True,
    False,
    Null
}

public readonly record struct JsonToken(
    JsonTokenKind Kind,
    int Depth,
    long Offset,
    int Length,
    string? Text);

public static class JsonIndexer
{
    private const int ChunkSize = 64 * 1024;

    public static List<JsonToken> Build(MMapFile file)
    {
        long offset = 0;
        long length = file.Length;

        var tokens = new List<JsonToken>(1024 * 1024);
        var state = new JsonReaderState(new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        int depth = 0;

        while (offset < length)
        {
            int size = (int)Math.Min(ChunkSize, length - offset);
            var buffer = new byte[size];
            file.Read(offset, buffer);

            var reader = new Utf8JsonReader(buffer, offset + size >= length, state);

            while (reader.Read())
            {
                var kind = Map(reader.TokenType);
                long tokenOffset = offset + reader.TokenStartIndex;
                int tokenLength = reader.HasValueSequence
                    ? (int)reader.ValueSequence.Length
                    : reader.ValueSpan.Length;

                string? text = reader.TokenType switch
                {
                    JsonTokenType.PropertyName => reader.GetString(),
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number => reader.HasValueSequence
                        ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                        : Encoding.UTF8.GetString(reader.ValueSpan),

                    JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null => reader.HasValueSequence
                        ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                        : Encoding.UTF8.GetString(reader.ValueSpan),
                    _ => null
                };

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    depth++;

                tokens.Add(new JsonToken(kind, depth, tokenOffset, tokenLength, text));

                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    depth--;
            }

            state = reader.CurrentState;
            offset += size;
        }

        return tokens;
    }

    private static JsonTokenKind Map(JsonTokenType t) => t switch
    {
        JsonTokenType.StartObject => JsonTokenKind.StartObject,
        JsonTokenType.EndObject => JsonTokenKind.EndObject,
        JsonTokenType.StartArray => JsonTokenKind.StartArray,
        JsonTokenType.EndArray => JsonTokenKind.EndArray,
        JsonTokenType.PropertyName => JsonTokenKind.PropertyName,
        JsonTokenType.String => JsonTokenKind.String,
        JsonTokenType.Number => JsonTokenKind.Number,
        JsonTokenType.True => JsonTokenKind.True,
        JsonTokenType.False => JsonTokenKind.False,
        JsonTokenType.Null => JsonTokenKind.Null,
        _ => throw new NotSupportedException()
    };
}
