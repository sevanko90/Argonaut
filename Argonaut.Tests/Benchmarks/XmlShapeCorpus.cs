using System.Buffers;
using System.Buffers.Text;

namespace Argonaut.Tests.Benchmarks;

/// <summary>
/// The document shapes the XML tree benchmarks compare: many tiny text elements, deep nesting, a
/// large root of city-model records, and records carrying one long text node each.
/// </summary>
public enum XmlShape
{
    /// <summary>A root of one-character text elements, 11 bytes each: the worst case for anything
    /// that keeps a record per node, and a container with hundreds of thousands of same-name
    /// siblings for XPath numbering to count.</summary>
    TinyElements,

    /// <summary>Chains 16 elements deep, each with a couple of attributes: many small containers
    /// and a deep ancestor stack, none of them large enough for the index to record.</summary>
    DeepNesting,

    /// <summary>An array-like root of CityGML records: namespaced names, attributes, text-only
    /// properties, nesting and a short coordinate list - the common shape of a large export.</summary>
    RecordRoot,

    /// <summary>Records carrying one 32 KB coordinate list each: the text a row is classified by
    /// looking past, and that the painter caps when it draws it.</summary>
    TextHeavy,
}

/// <summary>
/// Writes a compact, deterministic document of a given shape at exactly a given size, so a
/// benchmark can divide by the size it asked for and navigation benchmarks can seek into the
/// middle of it. Elements are written until the next one might not fit, the root is closed, and
/// the remainder is padded with trailing whitespace - an epilog XML allows, and one the scanner
/// does not treat as text outside the root.
///
/// The document is ASCII, uses <c>\n</c> line breaks and takes every value from the element's
/// index, so two writes of the same shape and size are byte-identical. Markup is written as UTF-8
/// literals and numbers straight into the stream, because a 64 MiB document is millions of
/// fragments written before every benchmark case - encoding each one as a string made setup, not
/// the benchmark, the slow part of a run.
///
/// The <c>--index-memory</c> harness keeps its own CityGML generator
/// (<see cref="IndexMemoryBaseline"/>): its recorded runs are compared against each other, so a
/// shared generator here would have moved every older number with it.
/// </summary>
public static class XmlShapeCorpus
{
    /// <summary>How deep a <see cref="XmlShape.DeepNesting"/> chain runs.</summary>
    public const int NestingDepth = 16;

    /// <summary>About how many bytes of coordinate list one <see cref="XmlShape.TextHeavy"/>
    /// record carries.</summary>
    public const int TextNodeBytes = 32 * 1024;

    private static ReadOnlySpan<byte> Declaration => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"u8;
    private static ReadOnlySpan<byte> RootOpen => "<r>\n"u8;
    private static ReadOnlySpan<byte> RootClose => "</r>\n"u8;

    private static ReadOnlySpan<byte> CityModelOpen =>
        "<core:CityModel xmlns:core=\"http://www.opengis.net/citygml/2.0\" xmlns:bldg=\"http://www.opengis.net/citygml/building/2.0\" xmlns:gml=\"http://www.opengis.net/gml\" xmlns:xAL=\"urn:oasis:names:tc:ciq:xsdschema:xAL:2.0\">\n"u8;

    private static ReadOnlySpan<byte> CityModelClose => "</core:CityModel>\n"u8;

    // One coordinate triple of a posList, repeated to length.
    private static ReadOnlySpan<byte> CoordinateTriple => "385000.000 5818000.000 30.000 "u8;

    /// <summary>The smallest document worth writing: below this the prolog, root and epilogue are
    /// most of the file.</summary>
    private const long MinimumBytes = 64 * 1024;

    /// <summary>
    /// <paramref name="count"/> offsets spread across a document and across the checkpoint
    /// stride, for benchmarks whose cost depends on how far a position is past the nearest
    /// checkpoint. Spreading across the document alone is not enough: evenly spaced offsets in a
    /// document whose length is a power of two are all multiples of the 64 KB stride, and with
    /// checkpoints that drift only a few bytes per stride (<see cref="XmlShape.TinyElements"/>)
    /// every one landed a whole stride past its checkpoint - the worst case, eight times. Each
    /// sample is therefore shifted by its own fraction of the stride as well.
    /// </summary>
    public static long[] SampleOffsets(long length, int count, int checkpointStride)
    {
        var offsets = new long[count];
        for (int sample = 0; sample < count; sample++)
            offsets[sample] = Math.Min(length - 1,
                length * (2 * sample + 1) / (2 * count) + (long)checkpointStride * sample / count);

        return offsets;
    }

    public static void Write(string path, XmlShape shape, long sizeBytes)
    {
        if (sizeBytes < MinimumBytes)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, $"A corpus document is at least {MinimumBytes} bytes.");

        using var stream = File.Create(path);
        var writer = new CorpusWriter(stream, HeadroomOf(shape));
        writer.Write(Declaration);
        writer.Write(shape == XmlShape.RecordRoot ? CityModelOpen : RootOpen);

        // Stop far enough from the end that the last element and the root's close tag both fit.
        long limit = sizeBytes - writer.Headroom;
        for (int element = 0; writer.Written < limit; element++)
            WriteElement(writer, shape, element);

        writer.Write(shape == XmlShape.RecordRoot ? CityModelClose : RootClose);
        writer.PadTo(sizeBytes);
    }

    /// <summary>Room one element and the root's close tag need; TextHeavy's element is the only
    /// one anywhere near it.</summary>
    private static int HeadroomOf(XmlShape shape)
        => shape == XmlShape.TextHeavy ? TextNodeBytes + 8 * 1024 : 8 * 1024;

    private static void WriteElement(CorpusWriter writer, XmlShape shape, int element)
    {
        switch (shape)
        {
            case XmlShape.TinyElements:
                writer.Write("  <a>"u8);
                writer.WriteDigit(element % 10);
                writer.Write("</a>\n"u8);
                break;

            case XmlShape.DeepNesting:
                WriteChain(writer, element, NestingDepth);
                break;

            case XmlShape.RecordRoot:
                WriteRecord(writer, element);
                break;

            case XmlShape.TextHeavy:
                WriteTextRecord(writer, element);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static void WriteChain(CorpusWriter writer, int element, int levels)
    {
        for (int level = 0; level < levels; level++)
        {
            writer.WriteIndent(level + 1);
            writer.Write("<n k=\""u8);
            writer.Write(level);
            writer.Write("\">\n"u8);
        }

        writer.WriteIndent(levels + 1);
        writer.Write("<leaf>v</leaf>\n"u8);

        for (int level = levels - 1; level >= 0; level--)
        {
            writer.WriteIndent(level + 1);
            writer.Write("</n>\n"u8);
        }
    }

    /// <summary>One city object as CityGML writes it: a member wrapping a building with an id,
    /// text-only properties, a code list attribute, a polygon's coordinates and an address - the
    /// mix of attributes, namespaces, one-line elements and nesting the XML index walks.</summary>
    private static void WriteRecord(CorpusWriter writer, int element)
    {
        writer.Write("  <core:cityObjectMember>\n    <bldg:Building gml:id=\"BLDG_"u8);
        writer.Write(element);
        writer.Write("\">\n      <gml:name>building "u8);
        writer.Write(element);
        writer.Write("</gml:name>\n      <core:creationDate>2026-"u8);
        writer.Write(1 + element % 12);
        writer.Write("-"u8);
        writer.Write(1 + element % 28);
        writer.Write("</core:creationDate>\n      <bldg:function codeSpace=\"http://www.sig3d.org/codelists/function.xml\">31001_"u8);
        writer.Write(1000 + element % 9 * 10);
        writer.Write("</bldg:function>\n      <bldg:measuredHeight uom=\"urn:adv:uom:m\">"u8);
        writer.Write((element % 4000) / 100.0, decimals: 2);
        writer.Write("</bldg:measuredHeight>\n      <bldg:lod2MultiSurface>\n        <gml:MultiSurface srsDimension=\"3\">\n"u8);
        writer.Write("          <gml:surfaceMember>\n            <gml:Polygon gml:id=\"P_"u8);
        writer.Write(element);
        writer.Write("\">\n              <gml:exterior><gml:LinearRing><gml:posList>"u8);
        for (int corner = 0; corner < 5; corner++)
        {
            if (corner > 0)
                writer.WriteAscii(' ');

            writer.Write(385000.0 + element % 1000 + corner, decimals: 3);
            writer.WriteAscii(' ');
            writer.Write(5818000.0 + element % 1000 + corner, decimals: 3);
            writer.WriteAscii(' ');
            writer.Write(30.0 + corner, decimals: 3);
        }

        writer.Write("</gml:posList></gml:LinearRing></gml:exterior>\n"u8);
        writer.Write("            </gml:Polygon>\n          </gml:surfaceMember>\n        </gml:MultiSurface>\n"u8);
        writer.Write("      </bldg:lod2MultiSurface>\n      <bldg:address><xAL:ThoroughfareName>High Street</xAL:ThoroughfareName>"u8);
        writer.Write("<xAL:ThoroughfareNumber>"u8);
        writer.Write(1 + element % 200);
        writer.Write("</xAL:ThoroughfareNumber></bldg:address>\n    </bldg:Building>\n  </core:cityObjectMember>\n"u8);
    }

    /// <summary>A record holding one long text element, wrapped as a posList and followed by a
    /// short name - what a text-heavy document spends its bytes on.</summary>
    private static void WriteTextRecord(CorpusWriter writer, int element)
    {
        writer.Write("  <rec id=\""u8);
        writer.Write(element);
        writer.Write("\">\n    <posList>"u8);
        int triples = TextNodeBytes / CoordinateTriple.Length;
        for (int triple = 0; triple < triples; triple++)
            writer.Write(CoordinateTriple);

        writer.Write("</posList>\n    <name>record "u8);
        writer.Write(element);
        writer.Write("</name>\n  </rec>\n"u8);
    }

    /// <summary>Writes UTF-8 into the stream, remembering how many bytes it has written. The
    /// stream is not relied on for length: <see cref="Written"/> is exact, so the document can be
    /// padded to a byte.</summary>
    private sealed class CorpusWriter(Stream stream, int headroom)
    {
        private readonly byte[] padding = new byte[headroom + 4096];
        private long written;

        public long Written => written;

        /// <summary>Room the caller must leave in the size for the last element and the close.</summary>
        public long Headroom => headroom;

        public void Write(ReadOnlySpan<byte> utf8)
        {
            stream.Write(utf8);
            written += utf8.Length;
        }

        public void Write(int value)
        {
            Span<byte> digits = stackalloc byte[16];
            Utf8Formatter.TryFormat(value, digits, out int length);
            Write(digits[..length]);
        }

        /// <summary>A fixed-point number, invariant, straight into the stream.</summary>
        public void Write(double value, int decimals)
        {
            Span<byte> digits = stackalloc byte[32];
            Utf8Formatter.TryFormat(value, digits, out int length, new StandardFormat('F', (byte)decimals));
            Write(digits[..length]);
        }

        public void WriteAscii(char value)
        {
            stream.WriteByte(checked((byte)value));
            written++;
        }

        public void WriteDigit(int digit)
        {
            stream.WriteByte((byte)('0' + digit));
            written++;
        }

        /// <summary><paramref name="depth"/> levels of two spaces.</summary>
        public void WriteIndent(int depth)
        {
            Span<byte> spaces = stackalloc byte[depth * 2];
            spaces.Fill((byte)' ');
            Write(spaces);
        }

        /// <summary>Fills the document to exactly <paramref name="sizeBytes"/> with whitespace.</summary>
        public void PadTo(long sizeBytes)
        {
            if (written > sizeBytes)
                throw new InvalidOperationException(
                    $"The corpus overran the requested size: {written} bytes written, {sizeBytes} asked for.");

            padding.AsSpan().Fill((byte)' ');
            long remaining = sizeBytes - written;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, padding.Length);
                stream.Write(padding, 0, chunk);
                written += chunk;
                remaining -= chunk;
            }
        }
    }
}
