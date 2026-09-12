using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Argonaut.Infrastructure.Unicode;

/// <summary>
/// The Unicode name of a character - "U+2028" is not an answer anyone can act on, "LINE SEPARATOR"
/// is. .NET carries character categories but no names, so the table is generated from the Unicode
/// Character Database by <c>scripts/make-unicode-names.py</c> and embedded compressed.
///
/// The shape is chosen to keep a name lookup off the allocation path, because this runs on every
/// caret move: the resource is inflated once into a single byte array and then binary-searched in
/// place, reading code points and offsets straight out of it with <see cref="BinaryPrimitives"/>.
/// Nothing is copied into dictionaries or arrays of strings; the only allocation per lookup is the
/// one name being asked about. That costs ~1.3MB resident against ~3MB for the equivalent
/// <c>Dictionary&lt;int, string&gt;</c>, and 40,000 fewer permanently-live objects.
///
/// Nothing is paid until the first lookup, so a session that never shows a caret position never
/// inflates the table.
/// </summary>
public static class UnicodeNames
{
    private const string ResourceName = "Argonaut.Assets.Unicode.UnicodeNames.deflate";

    /// <summary>Header: magic, format version, name count, range count.</summary>
    private const int HeaderBytes = 16;

    private static readonly Lazy<NameTable> Table = new(NameTable.Inflate, isThreadSafe: true);

    /// <summary>
    /// The character's Unicode name, or null when it has none - unassigned code points, and the
    /// noncharacters. Control characters report the name from NameAliases.txt (U+0085 is
    /// "NEXT LINE"), since their name field in UnicodeData.txt is literally "&lt;control&gt;".
    /// </summary>
    public static string? NameOf(int codePoint) => Table.Value.NameOf(codePoint);

    private sealed class NameTable
    {
        private readonly byte[] data;
        private readonly int nameCount;
        private readonly int rangeCount;

        private NameTable(byte[] data)
        {
            this.data = data;
            this.nameCount = ReadInt32(data, 8);
            this.rangeCount = ReadInt32(data, 12);
        }

        public static NameTable Inflate()
        {
            using var resource = typeof(NameTable).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
            using var inflater = new DeflateStream(resource, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            inflater.CopyTo(expanded);

            byte[] data = expanded.ToArray();
            if (data.Length < HeaderBytes || data[0] != (byte)'A' || data[1] != (byte)'U'
                || data[2] != (byte)'N' || data[3] != (byte)'M' || ReadInt32(data, 4) != 1)
            {
                throw new InvalidOperationException(
                    $"{ResourceName} is not a version 1 Unicode name table.");
            }

            return new NameTable(data);
        }

        public string? NameOf(int codePoint)
        {
            int found = IndexOfCodePoint(codePoint);
            if (found >= 0)
            {
                int start = ReadInt32(this.data, TextOffsetsStart + (found * 4));
                int end = ReadInt32(this.data, TextOffsetsStart + ((found + 1) * 4));
                return Encoding.UTF8.GetString(this.data, TextStart + start, end - start);
            }

            return NameFromRange(codePoint);
        }

        private int CodePointsStart => HeaderBytes;

        private int TextOffsetsStart => CodePointsStart + (this.nameCount * 4);

        private int RangesStart => TextOffsetsStart + ((this.nameCount + 1) * 4);

        private int TextStart => RangesStart + (this.rangeCount * 16);

        private int IndexOfCodePoint(int codePoint)
        {
            int low = 0;
            int high = this.nameCount - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int candidate = ReadInt32(this.data, CodePointsStart + (middle * 4));
                if (candidate == codePoint)
                    return middle;

                if (candidate < codePoint)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            return -1;
        }

        /// <summary>
        /// The ranges whose names UnicodeData.txt does not list one by one, because they follow a
        /// rule: a prefix plus the code point in hex (CJK, Tangut, private use), or - for Hangul
        /// syllables - a name composed from the jamo the syllable decomposes into.
        /// </summary>
        private string? NameFromRange(int codePoint)
        {
            for (int i = 0; i < this.rangeCount; i++)
            {
                int record = RangesStart + (i * 16);
                int start = ReadInt32(this.data, record);
                if (codePoint < start)
                    continue;

                int end = ReadInt32(this.data, record + 4);
                if (codePoint > end)
                    continue;

                int prefixStart = ReadInt32(this.data, record + 8);
                int prefixEnd = ReadInt32(this.data, record + 12);
                if (prefixEnd == prefixStart)
                    return HangulNames.Compose(codePoint);

                string prefix = Encoding.UTF8.GetString(
                    this.data, TextStart + prefixStart, prefixEnd - prefixStart);
                return prefix + codePoint.ToString("X4");
            }

            return null;
        }

        private static int ReadInt32(byte[] data, int offset)
            => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    /// <summary>
    /// Hangul syllable names are algorithmic: U+AC00 is HANGUL SYLLABLE GA, and the syllable
    /// decomposes into a leading jamo, a vowel and an optional trailing jamo whose short names
    /// concatenate (Unicode 3.12). Storing all 11,172 of them would be a third of the table.
    /// </summary>
    private static class HangulNames
    {
        private const int SyllableBase = 0xAC00;
        private const int VowelCount = 21;
        private const int TrailingCount = 28;
        private const int SyllablesPerLeading = VowelCount * TrailingCount;

        private static readonly string[] Leading =
            ["G", "GG", "N", "D", "DD", "R", "M", "B", "BB", "S", "SS", "", "J", "JJ", "C", "K",
             "T", "P", "H"];

        private static readonly string[] Vowel =
            ["A", "AE", "YA", "YAE", "EO", "E", "YEO", "YE", "O", "WA", "WAE", "OE", "YO", "U",
             "WEO", "WE", "WI", "YU", "EU", "YI", "I"];

        private static readonly string[] Trailing =
            ["", "G", "GG", "GS", "N", "NJ", "NH", "D", "L", "LG", "LM", "LB", "LS", "LT", "LP",
             "LH", "M", "B", "BS", "S", "SS", "NG", "J", "C", "K", "T", "P", "H"];

        public static string Compose(int codePoint)
        {
            int syllable = codePoint - SyllableBase;
            return "HANGUL SYLLABLE "
                + Leading[syllable / SyllablesPerLeading]
                + Vowel[syllable % SyllablesPerLeading / TrailingCount]
                + Trailing[syllable % TrailingCount];
        }
    }
}
