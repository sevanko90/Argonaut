namespace Argonaut.Features.Hex.Structs;

/// <summary>
/// The example the empty Structures tab offers: an <c>int</c>, a <c>float</c>, a <c>bool</c> and
/// a 256-character array, commented with each field's offset and size. The C# and C versions lay
/// out the same - 268 bytes, the name at 9, 3 bytes of trailing padding - so inserting one and
/// then the other changes nothing in the tree, which is itself a check.
/// </summary>
public static class StructSamples
{
    public const string CSharp = """
        [StructLayout(LayoutKind.Sequential)]
        unsafe struct Sample
        {
            public int Count;            // offset 0, 4 bytes
            public float Ratio;          // offset 4, 4 bytes
            public bool Enabled;         // offset 8, 1 byte: 0 = false, anything else true
            public fixed byte Name[256]; // offset 9, 256 bytes (a C# char is 2 bytes - byte keeps it 256)
        }                                // 265 bytes of fields + 3 of padding = 268, align 4

        """;

    public const string C = """
        struct Sample {
            int32_t count;     /* offset 0, 4 bytes */
            float   ratio;     /* offset 4, 4 bytes */
            bool    enabled;   /* offset 8, 1 byte */
            char    name[256]; /* offset 9, 256 bytes */
        };                     /* 265 bytes of fields + 3 of padding = 268, align 4 */

        """;

    public static string For(StructLanguage language) => language == StructLanguage.CSharp ? CSharp : C;
}
