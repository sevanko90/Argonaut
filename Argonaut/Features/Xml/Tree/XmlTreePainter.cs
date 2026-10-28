using System;
using System.Collections.Generic;
using Argonaut.Engine.Indexing.Trees;
using Argonaut.Engine.Text;
using Argonaut.Features.Xml.Indexing;
using Argonaut.Ui.Tree;

namespace Argonaut.Features.Xml.Tree;

/// <summary>
/// What an XML tree row says: markup as written, in runs by role - punctuation, the element's
/// name, each attribute's name and value, text - so the palette can colour each. An element that
/// holds only text is one row, <c>&lt;name&gt;text&lt;/name&gt;</c>; a collapsed element shows its
/// start tag, how many children it has, and its close tag. A text, CDATA section, comment or other
/// leaf too long to show is capped, followed by its size and an action that opens the raw view at
/// it. Rows have no marker: an XML sibling's position is part of its path, not of the row.
/// </summary>
public sealed class XmlTreePainter(XmlTreeText text) : ITreeRowPainter
{
    private const string CDataOpen = "<![CDATA[";
    private const string CDataClose = "]]>";

    public void AppendRuns(in TreeRow row, List<TreeRun> runs)
    {
        var node = row.Node;
        switch ((XmlNodeKind)node.FormatKind)
        {
            case XmlNodeKind.Element:
                AppendElementRow(row, runs);
                break;

            case XmlNodeKind.TextElement:
                AppendTextElement(node, runs);
                break;

            case XmlNodeKind.EmptyElement:
            {
                string name = AppendStartTag(node.ValueStart, runs, out long tagEnd, out _);
                if (!text.IsSelfClosing(tagEnd))
                    AppendCloseTag(name, runs);
                break;
            }

            case XmlNodeKind.Text:
                AppendContent(node.ValueStart, node.ValueEnd, runs);
                break;

            case XmlNodeKind.CData:
                AppendCData(node, runs);
                break;

            case XmlNodeKind.ProcessingInstruction or XmlNodeKind.XmlDeclaration:
                AppendInstruction(node, runs);
                break;

            default: // a comment or a DOCTYPE
                AppendWhole(node, TreeRunStyle.Comment, runs);
                break;
        }
    }

    public string? Marker(in TreeRow row) => null;

    private void AppendElementRow(in TreeRow row, List<TreeRun> runs)
    {
        if (row.Shape == TreeRowShape.Close)
        {
            AppendCloseTag(text.ElementName(row.Node.ValueStart), runs);
            return;
        }

        string name = AppendStartTag(row.Node.ValueStart, runs, out _, out _);
        if (row.IsExpanded)
            return;

        runs.Add(new TreeRun(text.Summary(row.Node.ValueStart), TreeRunStyle.Summary));
        AppendCloseTag(name, runs);
    }

    private void AppendTextElement(TreeNode node, List<TreeRun> runs)
    {
        string name = AppendStartTag(node.ValueStart, runs, out long tagEnd, out _);
        long closeStart = text.CloseStart(node.ValueStart, node.ValueEnd);
        string content = text.Content(tagEnd, closeStart, out bool truncated, out long contentOffset, out long fullLength);
        runs.Add(new TreeRun(content, TreeRunStyle.String));
        AppendCloseTag(name, runs);
        AppendTruncation(truncated, contentOffset, fullLength, runs);
    }

    /// <summary>A text run's trimmed content, with its truncation notice if it was capped.</summary>
    private void AppendContent(long start, long end, List<TreeRun> runs)
    {
        string content = text.Content(start, end, out bool truncated, out long contentOffset, out long fullLength);
        runs.Add(new TreeRun(content, TreeRunStyle.String));
        AppendTruncation(truncated, contentOffset, fullLength, runs);
    }

    private void AppendCData(TreeNode node, List<TreeRun> runs)
    {
        long contentStart = node.ValueStart + CDataOpen.Length;
        long contentEnd = Math.Max(contentStart, node.ValueEnd - CDataClose.Length);
        bool closed = node.ValueEnd - node.ValueStart >= CDataOpen.Length + CDataClose.Length;
        string content = text.Markup(contentStart, closed ? contentEnd : node.ValueEnd, out bool truncated);

        runs.Add(new TreeRun(CDataOpen, TreeRunStyle.Punctuation));
        runs.Add(new TreeRun(content, TreeRunStyle.String));
        if (closed && !truncated)
            runs.Add(new TreeRun(CDataClose, TreeRunStyle.Punctuation));
        AppendTruncation(truncated, contentStart, contentEnd - contentStart, runs);
    }

    /// <summary>A processing instruction or the XML declaration: <c>&lt;?</c>, the target, the
    /// data as written, <c>?&gt;</c>.</summary>
    private void AppendInstruction(TreeNode node, List<TreeRun> runs)
    {
        string shown = text.Markup(node.ValueStart, node.ValueEnd, out bool truncated);
        bool closed = !truncated && shown.EndsWith("?>", StringComparison.Ordinal) && shown.Length >= 4;
        int dataEnd = closed ? shown.Length - 2 : shown.Length;

        int targetEnd = 2;
        while (targetEnd < dataEnd && shown[targetEnd] is not (' ' or '\t' or '\r' or '\n' or '?'))
            targetEnd++;

        runs.Add(new TreeRun("<?", TreeRunStyle.Punctuation));
        if (targetEnd > 2)
            runs.Add(new TreeRun(shown[2..targetEnd], TreeRunStyle.Keyword));
        if (dataEnd > targetEnd)
            runs.Add(new TreeRun(shown[targetEnd..dataEnd], TreeRunStyle.Plain));
        if (closed)
            runs.Add(new TreeRun("?>", TreeRunStyle.Punctuation));
        AppendTruncation(truncated, node.ValueStart, node.ValueEnd - node.ValueStart, runs);
    }

    private void AppendWhole(TreeNode node, TreeRunStyle style, List<TreeRun> runs)
    {
        runs.Add(new TreeRun(text.Markup(node.ValueStart, node.ValueEnd, out bool truncated), style));
        AppendTruncation(truncated, node.ValueStart, node.ValueEnd - node.ValueStart, runs);
    }

    /// <summary>The size of what was capped and a way to read the rest: information first, then
    /// what appears on hover, so showing it moves nothing.</summary>
    private static void AppendTruncation(bool truncated, long contentOffset, long fullLength, List<TreeRun> runs)
    {
        if (!truncated)
            return;

        runs.Add(new TreeRun(ByteLengthText.Format(fullLength), TreeRunStyle.Note));
        runs.Add(new TreeRun("Open in raw", TreeRunStyle.Action, new ViewInRawLink(contentOffset), TreeRunIcon.FullText));
    }

    private static void AppendCloseTag(string name, List<TreeRun> runs)
    {
        runs.Add(new TreeRun("</", TreeRunStyle.Punctuation));
        runs.Add(new TreeRun(name, TreeRunStyle.Name));
        runs.Add(new TreeRun(">", TreeRunStyle.Punctuation));
    }

    /// <summary>
    /// Adds the runs of the start tag at <paramref name="start"/> and returns the element's name.
    /// The tag is split by its own syntax - name, then attributes of a name, <c>=</c> and a quoted
    /// value, up to <c>&gt;</c> or <c>/&gt;</c> - and so reads whatever a damaged or capped tag
    /// leaves: a value with no closing quote takes the rest.
    /// </summary>
    private string AppendStartTag(long start, List<TreeRun> runs, out long tagEnd, out bool truncated)
    {
        string tag = text.StartTag(start, out tagEnd, out truncated);

        runs.Add(new TreeRun("<", TreeRunStyle.Punctuation));
        int i = 1;
        int nameEnd = i;
        while (nameEnd < tag.Length && !IsNameEnd(tag[nameEnd]))
            nameEnd++;

        string name = tag[i..nameEnd];
        if (name.Length > 0)
            runs.Add(new TreeRun(name, TreeRunStyle.Name));
        i = nameEnd;

        while (i < tag.Length)
        {
            char c = tag[i];
            if (IsSpace(c))
            {
                int spaceEnd = i;
                while (spaceEnd < tag.Length && IsSpace(tag[spaceEnd]))
                    spaceEnd++;
                runs.Add(new TreeRun(tag[i..spaceEnd], TreeRunStyle.Plain));
                i = spaceEnd;
            }
            else if (c == '/' || c == '>')
            {
                int punctuationEnd = c == '/' && i + 1 < tag.Length && tag[i + 1] == '>' ? i + 2 : i + 1;
                runs.Add(new TreeRun(tag[i..punctuationEnd], TreeRunStyle.Punctuation));
                i = punctuationEnd;
            }
            else if (c == '=')
            {
                runs.Add(new TreeRun("=", TreeRunStyle.Punctuation));
                i++;
            }
            else if (c is '"' or '\'')
            {
                int close = tag.IndexOf(c, i + 1);
                int valueEnd = close < 0 ? tag.Length : close + 1;
                runs.Add(new TreeRun(tag[i..valueEnd], TreeRunStyle.AttributeValue));
                i = valueEnd;
            }
            else
            {
                int attributeEnd = i;
                while (attributeEnd < tag.Length && !IsAttributeNameEnd(tag[attributeEnd]))
                    attributeEnd++;
                if (attributeEnd == i)
                    attributeEnd++; // a stray byte: shown, never looped on
                runs.Add(new TreeRun(tag[i..attributeEnd], TreeRunStyle.AttributeName));
                i = attributeEnd;
            }
        }

        return name;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static bool IsNameEnd(char c) => IsSpace(c) || c is '/' or '>';

    private static bool IsAttributeNameEnd(char c) => IsSpace(c) || c is '=' or '/' or '>' or '"' or '\'';
}
