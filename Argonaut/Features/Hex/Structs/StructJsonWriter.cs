using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// A decoded struct as JSON, for Copy as JSON: a struct as an object of its fields - an anonymous
/// member's and a base's fields among them, as the language reads them - an array as an array,
/// and each value as its JSON kind. Text arrays are strings, decoded in full rather than as the
/// row shows them; enums are their names; pointers, and floats JSON has no number for, are
/// strings; bytes past the end of the document are null; padding is left out.
///
/// Bounded: past <see cref="MaxValues"/> values or <see cref="MaxBytes"/> of output it stops,
/// closes what it opened and says so, so an array of a million structs cannot run away with the
/// memory - the clipboard is not where a dump of that size belongs.
/// </summary>
public static class StructJsonWriter
{
    /// <summary>The most values written.</summary>
    public const int MaxValues = 200_000;

    /// <summary>The most output written, give or take the value that crosses it.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    /// <summary>The longest text array decoded, in bytes.</summary>
    private const int MaxTextBytes = 64 * 1024;

    /// <summary>The JSON, and whether it stopped short of the whole.</summary>
    public readonly record struct Result(string Json, bool Truncated);

    /// <summary><paramref name="node"/> - a row, or the tree's root for the whole mapping - as
    /// indented JSON.</summary>
    public static Result Write(StructTree tree, in StructNode node)
    {
        var output = new ArrayBufferWriter<byte>();
        var state = new State(tree, output);
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            state.Writer = writer;
            WriteValue(state, node);
        }

        return new Result(Encoding.UTF8.GetString(output.WrittenSpan), state.Truncated);
    }

    private sealed class State(StructTree tree, ArrayBufferWriter<byte> output)
    {
        public StructTree Tree { get; } = tree;

        public Utf8JsonWriter Writer { get; set; } = null!;

        public int Values { get; set; }

        public bool Truncated { get; set; }

        /// <summary>Whether to stop: counts a value, and stops past either limit.</summary>
        public bool Full()
        {
            if (Truncated)
                return true;
            if (++Values > MaxValues || output.WrittenCount + Writer.BytesPending > MaxBytes)
                Truncated = true;
            return Truncated;
        }
    }

    private static void WriteValue(State state, in StructNode node)
    {
        var writer = state.Writer;
        var nodes = state.Tree.Nodes;
        if (state.Full())
        {
            writer.WriteNullValue();
            return;
        }

        if (state.Tree.CanReadAsText(node) && state.Tree.ReadsAsText(node))
        {
            if (StructValueText.ReadText(state.Tree.Bytes, state.Tree.Start, node, MaxTextBytes) is { } text)
                writer.WriteStringValue(text.Text);
            else
                writer.WriteNullValue();
            return;
        }

        if (node.Value.IsArray)
        {
            writer.WriteStartArray();
            long count = nodes.ChildCount(node);
            for (long i = 0; i < count && !state.Truncated; i++)
                WriteValue(state, nodes.Child(node, i));
            writer.WriteEndArray();
            return;
        }

        if (node.Value.Struct is not null)
        {
            writer.WriteStartObject();
            WriteFields(state, node);
            writer.WriteEndObject();
            return;
        }

        WriteScalar(writer, state.Tree.ValueOf(node));
    }

    /// <summary>A struct's fields as properties; an anonymous member's and a base's are the
    /// struct's own, so they are written in place rather than nested.</summary>
    private static void WriteFields(State state, in StructNode node)
    {
        var nodes = state.Tree.Nodes;
        long count = nodes.ChildCount(node);
        for (long i = 0; i < count && !state.Truncated; i++)
        {
            var child = nodes.Child(node, i);
            if (child.Kind == StructNodeKind.Padding || (child.IsAnonymous && child.Value.Struct is null))
                continue;

            if (child.IsBase || child.IsAnonymous)
            {
                WriteFields(state, child);
                continue;
            }

            state.Writer.WritePropertyName(child.Label);
            WriteValue(state, child);
        }
    }

    /// <summary>A scalar as its JSON kind, from the text the row shows - the same reading, so the
    /// copy and the tree never disagree.</summary>
    private static void WriteScalar(Utf8JsonWriter writer, StructText value)
    {
        string text = value.Text;
        switch (value.Kind)
        {
            case StructTextKind.Missing:
                writer.WriteNullValue();
                return;

            case StructTextKind.Number:
                if (text.StartsWith("0x", StringComparison.Ordinal) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                    writer.WriteStringValue(text);
                else
                    writer.WriteRawValue(text, skipInputValidation: false);
                return;

            case StructTextKind.Keyword:
                if (text == "false")
                    writer.WriteBooleanValue(false);
                else if (text == "true" || text.StartsWith("true (", StringComparison.Ordinal))
                    writer.WriteBooleanValue(true);
                else
                    writer.WriteStringValue(WithoutNumber(text));
                return;

            case StructTextKind.Text:
                // A character: 'a' (97) as "a", a control character (shown as its number) as it.
                if (text.StartsWith('\'') && text.IndexOf("' (", StringComparison.Ordinal) is var close and > 0)
                    writer.WriteStringValue(text[1..close]);
                else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
                    writer.WriteNumberValue(code);
                else
                    writer.WriteStringValue(text);
                return;

            default:
                writer.WriteStringValue(text);
                return;
        }
    }

    /// <summary>An enum's names without the number after them: <c>Read | Write (3)</c> as
    /// <c>Read | Write</c>.</summary>
    private static string WithoutNumber(string text)
    {
        int open = text.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && text.EndsWith(')') ? text[..open] : text;
    }
}
