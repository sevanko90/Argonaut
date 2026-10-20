using System.Text;

namespace Argonaut.Features.Raw.Recipes;

/// <summary>
/// How a recipe's literal text is shown in a one-line text box: a line break as <c>\n</c>, a tab
/// as <c>\t</c>, a carriage return as <c>\r</c>, and a backslash as <c>\\</c> - so a separator of
/// ",&#x0A;" can be seen and typed as <c>,\n</c>. A backslash before anything else is kept as
/// typed, which is what someone writing a Windows path or a regex into a template expects.
/// </summary>
public static class RecipeFieldText
{
    public static string ToDisplay(string text)
    {
        var shown = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            shown.Append(c switch
            {
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '\\' => @"\\",
                _ => c.ToString(),
            });
        }

        return shown.ToString();
    }

    public static string FromDisplay(string shown)
    {
        var text = new StringBuilder(shown.Length);
        for (int i = 0; i < shown.Length; i++)
        {
            char c = shown[i];
            if (c == '\\' && i + 1 < shown.Length)
            {
                char next = shown[i + 1];
                char? meant = next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    _ => null,
                };

                if (meant is { } escaped)
                {
                    text.Append(escaped);
                    i++;
                    continue;
                }
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
