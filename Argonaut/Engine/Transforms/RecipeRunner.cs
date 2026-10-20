using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Argonaut.Engine.Bytes;

namespace Argonaut.Engine.Transforms;

/// <summary>
/// Runs a <see cref="Recipe"/> over a settled source into a buffer writer, holding one line at a
/// time (see <see cref="TransformLineReader"/>) - so the memory a run costs is its output and
/// nothing proportional to its input.
///
/// Synchronous and blocking: a preview runs it on a few kilobytes, and applying it to a
/// document belongs on a background thread.
/// </summary>
public static class RecipeRunner
{
    /// <exception cref="TransformException">The input does not fit the recipe; the message is for
    /// the user.</exception>
    public static void Run(Recipe recipe, TransformDirection direction, IByteSource input, IBufferWriter<byte> output,
        CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (direction == TransformDirection.Forward)
            Wrap(recipe, input, output, stopping);
        else
            Unwrap(recipe, input, output, stopping);
    }

    private static void Wrap(Recipe recipe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        var literals = SplitTemplate(recipe.Template);
        byte[] separator = Encoding.UTF8.GetBytes(recipe.Separator);

        using var reader = new TransformLineReader(input);
        output.Write(Encoding.UTF8.GetBytes(recipe.Open));
        bool first = true;
        while (reader.TryRead(out var line))
        {
            reader.ThrowIfStopping(stopping);
            if (recipe.TrimLines)
                line = line.Trim(TransformLineReader.SpacesAndTabs);

            if (recipe.SkipBlankLines && line.IsEmpty)
                continue;

            if (!first)
                output.Write(separator);

            first = false;
            for (int i = 0; i < literals.Count; i++)
            {
                if (i > 0)
                    TextEscaping.Escape(recipe.Escape, line, output);

                output.Write(literals[i]);
            }
        }

        output.Write(Encoding.UTF8.GetBytes(recipe.Close));
    }

    /// <summary>
    /// Takes apart what <see cref="Wrap"/> produced: each item becomes one output line. Works a
    /// line at a time, so several items on one line (<c>('a','b')</c>) and one item per line both
    /// read the same way, and whitespace round the template's literals, separator and wrapper is
    /// tolerated - which is what lets it read a list typed by hand, not only one this made.
    /// </summary>
    private static void Unwrap(Recipe recipe, IByteSource input, IBufferWriter<byte> output, CancellationToken stopping)
    {
        var literals = SplitTemplate(recipe.Template);
        if (literals.Count != 2)
            throw new TransformException($"To reverse it, the template needs exactly one {Recipe.LinePlaceholder}.");

        var shape = new ItemShape(
            literals[0].AsSpan().TrimStart(TransformLineReader.SpacesAndTabs).ToArray(),
            literals[1].AsSpan().TrimEnd(TransformLineReader.SpacesAndTabs).ToArray(),
            Encoding.UTF8.GetBytes(recipe.Separator.Trim()),
            recipe.Escape);
        byte[] open = Encoding.UTF8.GetBytes(recipe.Open.Trim());
        byte[] close = Encoding.UTF8.GetBytes(recipe.Close.Trim());

        using var reader = new TransformLineReader(input);
        var items = new List<Range>();
        bool openPending = open.Length > 0;
        bool first = true;
        while (reader.TryRead(out var line))
        {
            reader.ThrowIfStopping(stopping);
            line = line.Trim(TransformLineReader.SpacesAndTabs);
            if (line.IsEmpty)
                continue;

            if (openPending)
            {
                openPending = false;
                if (line.StartsWith(open))
                    line = line[open.Length..].TrimStart(TransformLineReader.SpacesAndTabs);
            }

            items.Clear();
            bool parsed = false;
            if (close.Length > 0 && line.EndsWith(close))
            {
                var inside = line[..^close.Length].TrimEnd(TransformLineReader.SpacesAndTabs);
                parsed = shape.TryParse(inside, items, out _);
            }

            if (!parsed)
            {
                items.Clear();
                if (!shape.TryParse(line, items, out string? problem))
                    throw new TransformException($"Line {reader.LineNumber}: {problem}.");
            }

            foreach (var item in items)
            {
                if (!first)
                    output.Write(reader.NewLine);

                first = false;
                var content = line[item];
                try
                {
                    TextEscaping.Unescape(recipe.Escape, content, output);
                }
                catch (TransformException ex)
                {
                    throw new TransformException($"Line {reader.LineNumber}: the item {ex.Message}.");
                }
            }
        }

        if (!first && reader.EndedWithNewline)
            output.Write(reader.NewLine);
    }

    /// <summary>One item's literals, as the reversed template looks for them: the text before
    /// the placeholder without its indentation, the text after it, and the separator trimmed.</summary>
    private readonly record struct ItemShape(byte[] Before, byte[] After, byte[] Separator, TextEscape Escape)
    {
        /// <summary>The items on one line, as ranges of their escaped contents.</summary>
        public bool TryParse(ReadOnlySpan<byte> line, List<Range> items, out string? problem)
        {
            int at = 0;
            while (true)
            {
                at = SkipWhitespace(line, at);
                if (at == line.Length)
                {
                    problem = null;
                    return true;
                }

                if (!line[at..].StartsWith(Before))
                {
                    problem = $"expected '{Encoding.UTF8.GetString(Before)}'";
                    return false;
                }

                at += Before.Length;
                int start = at;
                int end;
                if (After.Length > 0)
                {
                    end = FindAfter(line, at);
                    if (end < 0)
                    {
                        problem = $"no closing '{Encoding.UTF8.GetString(After)}'";
                        return false;
                    }

                    at = end + After.Length;
                }
                else
                {
                    int separator = Separator.Length > 0 ? line[at..].IndexOf(Separator) : -1;
                    end = separator < 0 ? line.Length : at + separator;
                    at = end;
                }

                // A bare template ("{line}") has nothing to say where the item stops but the
                // separator, so the spaces round it belong to the layout, not the item.
                if (Before.Length == 0 && After.Length == 0)
                {
                    while (start < end && IsWhitespace(line[start]))
                        start++;
                    while (end > start && IsWhitespace(line[end - 1]))
                        end--;
                }

                items.Add(start..end);

                at = SkipWhitespace(line, at);
                if (at < line.Length && Separator.Length > 0)
                {
                    if (!line[at..].StartsWith(Separator))
                    {
                        problem = $"expected '{Encoding.UTF8.GetString(Separator)}' after an item";
                        return false;
                    }

                    at += Separator.Length;
                }
            }
        }

        /// <summary>Where <see cref="After"/> next occurs outside an escape, or -1.</summary>
        private int FindAfter(ReadOnlySpan<byte> line, int from)
        {
            byte doubled = Escape switch
            {
                TextEscape.Sql => (byte)'\'',
                TextEscape.Csv => (byte)'"',
                _ => 0,
            };

            for (int i = from; i < line.Length; i++)
            {
                if (Escape == TextEscape.Json && line[i] == (byte)'\\')
                {
                    i++;
                    continue;
                }

                if (doubled != 0 && line[i] == doubled && i + 1 < line.Length && line[i + 1] == doubled)
                {
                    i++;
                    continue;
                }

                if (line[i..].StartsWith(After))
                    return i;
            }

            return -1;
        }
    }

    /// <summary>The template's literal text either side of each placeholder, as UTF-8 - one
    /// more literal than there are placeholders.</summary>
    private static List<byte[]> SplitTemplate(string template)
    {
        var literals = new List<byte[]>();
        int from = 0;
        int at;
        while ((at = template.IndexOf(Recipe.LinePlaceholder, from, StringComparison.Ordinal)) >= 0)
        {
            literals.Add(Encoding.UTF8.GetBytes(template[from..at]));
            from = at + Recipe.LinePlaceholder.Length;
        }

        literals.Add(Encoding.UTF8.GetBytes(template[from..]));
        return literals;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> line, int at)
    {
        while (at < line.Length && IsWhitespace(line[at]))
            at++;
        return at;
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t';
}
