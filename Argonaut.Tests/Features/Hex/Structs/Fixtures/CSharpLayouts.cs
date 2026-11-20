// Compiled into the tests and read as text by StructLayoutCSharpTests: every struct here is laid
// out by the struct decoder and compared with where the runtime puts its fields - in memory for
// InMemory, as marshalled for Marshalled. Keep to what the decoder reads; a struct it cannot
// read fails the test that walks this file.
using System;
using System.Runtime.InteropServices;

namespace Argonaut.Tests.Features.Hex.Structs.Fixtures.InMemory
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Basic { public byte A; public int B; public short C; public double D; }

    public struct Tail { public double D; public byte C; }

    public struct AllPrimitives
    {
        public byte A; public sbyte B; public short C; public ushort D; public int E; public uint F;
        public long G; public ulong H; public float I; public double J; public bool K; public char L;
        public Half M; public nint N; public nuint O;
    }

    public unsafe struct Pointers { public byte A; public void* P; public int** Pp; public IntPtr I; public byte B; }

    public unsafe struct Sample
    {
        public int Count;            // offset 0, 4 bytes
        public float Ratio;          // offset 4, 4 bytes
        public bool Enabled;         // offset 8, 1 byte: 0 = false, anything else true
        public fixed byte Name[256]; // offset 9, 256 bytes
    }

    public unsafe struct FixedChars { public byte Tag; public fixed char Name[256]; public byte After; }

    public struct Nested { public byte Tag; public Basic Inner; public Tail Last; }

    public struct WithMembers
    {
        public const int Count = 4;
        public static int Shared;
        public int A { get; set; }
        public byte B;
        public int Computed => A * 2;
        public long C { get; init; }
        public int WithBody { get { return A; } }
        public WithMembers(int a) { A = a; B = 0; C = 0; }
        public void Touch() { }
        public short D, E;
    }

    public record struct Point(int X, double Y);

    public struct Empty { }

    public struct HasEmpty { public byte A; public Empty E; public int B; }

    public struct FrameworkNames { public System.Byte A; public Int32 B; public System.Double C; public Char D; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Pack1 { public byte A; public int B; public short C; public double D; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct Pack2 { public byte A; public int B; public byte C; public long D; }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    public struct Sized { public int A; public byte B; }

    [StructLayout(LayoutKind.Explicit)]
    public struct Overlay
    {
        [FieldOffset(0)] public int I;
        [FieldOffset(0)] public float F;
        [FieldOffset(4)] public byte B;
        [FieldOffset(8)] public long L;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public unsafe struct ExplicitSized { [FieldOffset(2)] public short A; [FieldOffset(8)] public fixed byte Bytes[4]; }

    public enum Small : byte { A, B }

    [Flags]
    public enum Access { None = 0, Read = 1, Write = Read << 1, Both = Read | Write }

    public enum Wide : long { Big = 1L << 40 }

    public struct Enums { public byte A; public Small S; public Access Access; public Wide W; public byte B; }
}

namespace Argonaut.Tests.Features.Hex.Structs.Fixtures.Marshalled
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WinBools
    {
        public byte A;
        [MarshalAs(UnmanagedType.Bool)] public bool B;
        [MarshalAs(UnmanagedType.U1)] public bool C;
        public int D;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WideText
    {
        public byte A;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 5)] public string Name;
        public byte B;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByValues
    {
        public short A;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public int[] Values;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 10)] public string Name;
        public byte B;
    }
}
