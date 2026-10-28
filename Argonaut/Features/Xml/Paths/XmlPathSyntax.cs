using System;
using System.Collections.Generic;
using System.Text;

namespace Argonaut.Features.Xml.Paths;

/// <summary>
/// The XPath subset the tree writes and reads: an absolute path of <c>/</c>-separated steps, each a
/// name as the document writes it (prefix included), <c>*</c>, <c>text()</c>, <c>comment()</c> or
/// <c>processing-instruction(...)</c>, with an optional positive integer predicate - e.g.
/// <c>/core:CityModel/core:member[2]/text()[1]</c>. Names are lexical: matched by their bytes, with
/// no namespace resolution. Parsing and formatting only; walking a document by a path is
/// <c>XmlTreePaths</c>'.
/// </summary>
public static class XmlPathSyntax
{
    internal enum StepKind : byte
    {
        /// <summary>An element by its name as written.</summary>
        Element,

        /// <summary><c>*</c>: an element of any name.</summary>
        AnyElement,

        /// <summary><c>text()</c>: text and CDATA together.</summary>
        Text,

        Comment,

        /// <summary>A processing instruction, by target when <see cref="Step.Name"/> is set.</summary>
        ProcessingInstruction,
    }

    /// <summary>One step of a parsed path. <paramref name="Position"/> is the 1-based predicate, or
    /// 0 when the step has none.</summary>
    internal readonly record struct Step(StepKind Kind, string? Name, int Position);

    /// <summary>What a refused path is told is accepted; every message ends with it.</summary>
    internal const string SupportedSyntax =
        "Supported: an absolute path of / separated steps - a name, *, text(), comment() or processing-instruction(...) - "
        + "each with an optional position like [2], e.g. /root/child[2].";

    /// <summary>A step spelled as a path writes it, predicate included.</summary>
    internal static string FormatStep(Step step)
    {
        string name = step.Kind switch
        {
            StepKind.Element => step.Name!,
            StepKind.AnyElement => "*",
            StepKind.Text => "text()",
            StepKind.Comment => "comment()",
            _ => step.Name is null ? "processing-instruction()" : $"processing-instruction('{step.Name}')",
        };

        return step.Position > 0 ? $"{name}[{step.Position}]" : name;
    }

    /// <summary>The first <paramref name="count"/> steps as a path; <c>/</c> for none.</summary>
    internal static string FormatPath(IReadOnlyList<Step> steps, int count)
    {
        if (count == 0)
            return "/";

        var path = new StringBuilder();
        for (int i = 0; i < count; i++)
            path.Append('/').Append(FormatStep(steps[i]));
        return path.ToString();
    }

    internal static bool TryParse(string path, out List<Step> steps, out string? error)
    {
        steps = [];
        error = null;

        string s = path.Trim();
        if (s.Length == 0)
        {
            error = $"Enter an XPath, e.g. /root/child[2]. {SupportedSyntax}";
            return false;
        }

        if (s[0] != '/')
            return Refuse($"A path starts with '/'. {SupportedSyntax}", out error);

        if (s.Length == 1)
            return true;

        int i = 1;
        while (true)
        {
            if (!TryReadStep(s, ref i, out var step, out error))
                return false;

            steps.Add(step);
            if (i == s.Length)
                return true;

            i++; // the '/' TryReadStep stopped at
        }
    }

    private static bool Refuse(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static bool TryReadStep(string s, ref int i, out Step step, out string? error)
    {
        step = default;
        error = null;
        int start = i;

        // The test runs to a '[' or '/', except that a quoted literal may hold either.
        char quote = '\0';
        while (i < s.Length && (quote != '\0' || (s[i] != '[' && s[i] != '/')))
        {
            if (quote == '\0' && s[i] is '\'' or '"')
                quote = s[i];
            else if (s[i] == quote)
                quote = '\0';
            i++;
        }

        string test = s[start..i];
        if (quote != '\0' || test.Length == 0)
            return Refuse($"Can't read the step at position {start}: '{s[start..]}'. {SupportedSyntax}", out error);

        int position = 0;
        if (i < s.Length && s[i] == '[')
        {
            int close = s.IndexOf(']', i);
            if (close < 0)
                return Refuse($"Unterminated '[' at position {i}. {SupportedSyntax}", out error);

            string digits = s[(i + 1)..close];
            if (digits.Length == 0 || digits.AsSpan().ContainsAnyExceptInRange('0', '9') || !int.TryParse(digits, out position) || position < 1)
                return Refuse($"Only a positive whole number is supported in '[...]', not '[{digits}]'. {SupportedSyntax}", out error);

            i = close + 1;
            if (i < s.Length && s[i] != '/')
                return Refuse($"Unexpected '{s[i]}' at position {i}; only one position is supported per step. {SupportedSyntax}", out error);
        }

        if (!TryTest(test, position, out step))
            return Refuse($"'{test}' is not a supported step. {SupportedSyntax}", out error);

        return true;
    }

    private static bool TryTest(string test, int position, out Step step)
    {
        step = default;
        switch (test)
        {
            case "*":
                step = new Step(StepKind.AnyElement, null, position);
                return true;
            case "text()":
                step = new Step(StepKind.Text, null, position);
                return true;
            case "comment()":
                step = new Step(StepKind.Comment, null, position);
                return true;
            case "processing-instruction()":
                step = new Step(StepKind.ProcessingInstruction, null, position);
                return true;
        }

        const string Instruction = "processing-instruction(";
        if (test.StartsWith(Instruction, StringComparison.Ordinal) && test.EndsWith(')'))
        {
            string literal = test[Instruction.Length..^1];
            if (literal.Length >= 3 && literal[0] == literal[^1] && literal[0] is '\'' or '"' && IsQualifiedName(literal[1..^1]))
            {
                step = new Step(StepKind.ProcessingInstruction, literal[1..^1], position);
                return true;
            }

            return false;
        }

        if (!IsQualifiedName(test))
            return false;

        step = new Step(StepKind.Element, test, position);
        return true;
    }

    /// <summary>A name as a document writes one: at most one colon, with something either side,
    /// and no character that ends a name or starts XPath syntax.</summary>
    private static bool IsQualifiedName(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]) || name[0] is '-' or '.')
            return false;

        int colons = 0;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == ':')
            {
                if (++colons > 1 || i == 0 || i == name.Length - 1)
                    return false;
            }
            else if (!(char.IsLetter(c) || c == '_' || c >= 0x80 || char.IsAsciiDigit(c) || c is '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
