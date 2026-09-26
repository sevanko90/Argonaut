using System.Text;

namespace Argonaut.Tests.Support;

/// <summary>
/// Seeded random JSON for comparing a scanner against <c>Utf8JsonReader</c>: nesting, strings full
/// of brackets, quotes, slashes, escapes and multi-byte characters, and whitespace runs long enough
/// to slide every construct across a 64-byte block boundary somewhere. Optionally JSONC - comments
/// hiding brackets and quotes, and trailing commas - which the app's reader accepts.
/// </summary>
internal static class RandomJson
{
    /// <summary>A root array of <paramref name="elements"/> random values.</summary>
    public static byte[] Document(Random random, int elements, bool jsonc = false, int maxDepth = 6)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < elements; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(Gap(random, jsonc));
            AppendValue(text, random, depth: 0, maxDepth, jsonc);
            text.Append(Gap(random, jsonc));
        }

        if (jsonc && random.Next(2) == 0)
            text.Append(',');
        return Encoding.UTF8.GetBytes(text.Append(']').ToString());
    }

    /// <summary>A root array of <paramref name="elements"/> copies of a large nested value, so
    /// the document has containers well past a small promotion size at several depths.</summary>
    public static byte[] LargeContainers(Random random, int elements, bool jsonc = false)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < elements; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(Gap(random, jsonc));
            if (random.Next(3) == 0)
            {
                text.Append("{\"items\":[");
                for (int n = 0, count = random.Next(10, 60); n < count; n++)
                {
                    if (n > 0)
                        text.Append(',').Append(Gap(random, jsonc));
                    AppendValue(text, random, depth: 2, maxDepth: 5, jsonc);
                }

                text.Append(jsonc && random.Next(4) == 0 ? ",]}" : "]}");
            }
            else
            {
                AppendValue(text, random, depth: 0, maxDepth: 4, jsonc);
            }
        }

        return Encoding.UTF8.GetBytes(text.Append(']').ToString());
    }

    private static void AppendValue(StringBuilder text, Random random, int depth, int maxDepth, bool jsonc)
    {
        int pick = random.Next(depth >= maxDepth ? 5 : 7);
        switch (pick)
        {
            case 0:
                text.Append(random.Next(-100000, 100000));
                break;
            case 1:
                text.Append(random.Next(2) == 0 ? "true" : "false");
                break;
            case 2:
                text.Append("null");
                break;
            case 3:
            case 4:
                AppendString(text, random);
                break;
            case 5:
                text.Append('[');
                AppendMembers(text, random, depth, maxDepth, jsonc, named: false);
                text.Append(']');
                break;
            default:
                text.Append('{');
                AppendMembers(text, random, depth, maxDepth, jsonc, named: true);
                text.Append('}');
                break;
        }
    }

    private static void AppendMembers(StringBuilder text, Random random, int depth, int maxDepth, bool jsonc, bool named)
    {
        int count = random.Next(8);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(Gap(random, jsonc));
            if (named)
            {
                AppendString(text, random);
                // System.Text.Json does not accept a comment between a name and its colon.
                text.Append(Gap(random, jsonc: false)).Append(':').Append(Gap(random, jsonc));
            }

            AppendValue(text, random, depth + 1, maxDepth, jsonc);
        }

        if (count > 0 && jsonc && random.Next(4) == 0)
            text.Append(',');
        text.Append(Gap(random, jsonc));
    }

    private static readonly string[] StringPieces =
    {
        "a", "key", "{", "}", "[", "]", ",", ":", "/", "//", "/*", "*/", "\\\"", "\\\\", "\\\\\\\"", "\\n", "\\u00e9", "é", "日本", "😀", " ",
    };

    private static void AppendString(StringBuilder text, Random random)
    {
        text.Append('"');
        for (int i = 0, n = random.Next(30); i < n; i++)
            text.Append(StringPieces[random.Next(StringPieces.Length)]);
        text.Append('"');
    }

    private static readonly string[] Comments =
    {
        "/* ] } */", "/* \" [ */", "// ] } \"\n", "/**/", "/*/ } */", "/* ** / */", "//\n",
    };

    /// <summary>Whitespace between tokens, and in JSONC sometimes a comment inside it.</summary>
    private static string Gap(Random random, bool jsonc)
    {
        string space = random.Next(4) switch
        {
            0 => "",
            1 => " ",
            2 => "\n  ",
            _ => new string(' ', random.Next(70)),
        };

        return jsonc && random.Next(5) == 0 ? space + Comments[random.Next(Comments.Length)] + " " : space;
    }
}
