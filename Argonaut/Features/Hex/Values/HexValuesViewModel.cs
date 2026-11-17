using System;
using System.Collections.Generic;
using Argonaut.Engine.Bytes;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Hex.Values;

/// <summary>
/// One reading in the Values tab: the caret's group read as one type, in the tab's byte order. A
/// reading there are too few bytes left for - a short last group - is <c>-</c>.
/// </summary>
/// <param name="ByteCount">How many bytes the reading covers, which hovering it marks in the grid:
/// the group's, or a UTF-8 character's.</param>
public sealed record HexValueRow(string Name, int ByteCount, string Value);

/// <summary>
/// The side panel's Values tab: the caret's group read as the types that size can hold - a byte
/// as an unsigned and signed number and as a character, two, four or eight bytes as integers,
/// floating point and, at four and eight, a Unix time. The grouping is what says how wide the
/// value is, so the bytes read are the ones the caret marks in the dump, and choosing another
/// width regroups the dump to match.
///
/// One byte order at a time, a switch starting at this machine's and remembered: once it suits a
/// file it rarely changes. Reading a group costs nothing however large the document is.
/// </summary>
public sealed class HexValuesViewModel(ByteOrder order = ByteOrder.Little, Action<ByteOrder>? remember = null) : ObservableObject
{
    /// <summary>The most bytes a reading looks at: the widest group, which is also as far as a
    /// UTF-8 character can run.</summary>
    public const int MaxBytes = 8;

    private const string Missing = "-";

    /// <summary>This machine's byte order, which the switch starts at and its reset goes back to.</summary>
    public static readonly ByteOrder SystemOrder = BitConverter.IsLittleEndian ? ByteOrder.Little : ByteOrder.Big;

    private readonly byte[] bytes = new byte[MaxBytes];
    private long start;
    private int available;
    private int size = 1;
    private ByteOrder order = order;

    public bool HasBytes { get; private set; }

    /// <summary>What is being read: "32-bit".</summary>
    public string SizeText => $"{this.size * 8}-bit";

    /// <summary>Where the group starts, in hex as the gutter shows it.</summary>
    public string OffsetText { get; private set; } = string.Empty;

    /// <summary>The group's bytes, in file order.</summary>
    public string BytesText { get; private set; } = string.Empty;

    public IReadOnlyList<HexValueRow> Rows { get; private set; } = [];

    /// <summary>The group's bits, four bytes to a line, in file order.</summary>
    public IReadOnlyList<string> BitsLines { get; private set; } = [];

    /// <summary>The byte order the readings use.</summary>
    public ByteOrder Order
    {
        get => this.order;
        set
        {
            if (value == this.order)
                return;

            this.order = value;
            remember?.Invoke(value);
            Rows = HasBytes ? Read(this.bytes.AsSpan(0, this.available), this.size, value) : [];
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLittleEndian));
            OnPropertyChanged(nameof(IsBigEndian));
            OnPropertyChanged(nameof(IsSystemOrder));
            OnPropertyChanged(nameof(Rows));
        }
    }

    /// <summary>For the switch's two buttons: choosing one sets the order.</summary>
    public bool IsLittleEndian
    {
        get => this.order == ByteOrder.Little;
        set
        {
            if (value)
                Order = ByteOrder.Little;
            else
                OnPropertyChanged();
        }
    }

    public bool IsBigEndian
    {
        get => this.order == ByteOrder.Big;
        set
        {
            if (value)
                Order = ByteOrder.Big;
            else
                OnPropertyChanged();
        }
    }

    /// <summary>Whether the order is this machine's - when it is, the reset has nothing to do.</summary>
    public bool IsSystemOrder => this.order == SystemOrder;

    /// <summary>What the reset does, for its tooltip: "Use this Mac's byte order (little-endian)".</summary>
    public static string SystemOrderText
        => $"Use {(OperatingSystem.IsMacOS() ? "this Mac's" : "this computer's")} byte order ({(SystemOrder == ByteOrder.Little ? "little" : "big")}-endian)";

    /// <summary>Goes back to this machine's byte order.</summary>
    public void UseSystemOrder() => Order = SystemOrder;

    /// <summary>The bytes a hovered reading covers, which the grid marks; null when none is.</summary>
    public ByteRange? HighlightedRange
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>
    /// Reads the group of <paramref name="groupSize"/> bytes at <paramref name="offset"/>.
    /// <paramref name="bytes"/> starts there and may run on past the group (up to
    /// <see cref="MaxBytes"/>), so a UTF-8 character can be read to its end; fewer than the group
    /// near the end of the document.
    /// </summary>
    public void Show(long offset, ReadOnlySpan<byte> bytes, int groupSize, int offsetDigits)
    {
        bytes = bytes[..Math.Min(bytes.Length, MaxBytes)];
        bytes.CopyTo(this.bytes);
        this.start = offset;
        this.available = bytes.Length;
        this.size = groupSize;
        HasBytes = !bytes.IsEmpty;
        var group = bytes[..Math.Min(bytes.Length, groupSize)];
        OffsetText = HasBytes ? "0x" + offset.ToString("X").PadLeft(offsetDigits, '0') : string.Empty;
        BytesText = HasBytes ? HexCopy.AsHex(group) : string.Empty;
        Rows = HasBytes ? Read(bytes, groupSize, this.order) : [];
        BitsLines = HasBytes ? Bits(group) : [];
        HighlightedRange = null;
        OnPropertyChanged(nameof(HasBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(OffsetText));
        OnPropertyChanged(nameof(BytesText));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(BitsLines));
    }

    /// <summary>Marks the bytes <paramref name="row"/> reads in the grid, or nothing.</summary>
    public void Highlight(HexValueRow? row)
        => HighlightedRange = row is null || !HasBytes ? null : new ByteRange(this.start, Math.Min(row.ByteCount, this.available));

    /// <summary>
    /// The readings of a group of <paramref name="size"/> bytes at the start of
    /// <paramref name="bytes"/>, in <paramref name="order"/>. A group cut short by the end of the
    /// document reads as <c>-</c>, except a lone byte, which is always whole.
    /// </summary>
    public static IReadOnlyList<HexValueRow> Read(ReadOnlySpan<byte> bytes, int size, ByteOrder order)
    {
        if (size == 1)
        {
            byte first = bytes[0];
            var character = bytes[..Math.Min(4, bytes.Length)];
            return
            [
                new("uint8", 1, ByteReading.Integer(first)),
                new("int8", 1, ByteReading.Integer((sbyte)first)),
                new("ASCII", 1, ByteReading.AsciiName(first) ?? Missing),
                new("UTF-8", character.Length, ByteReading.Utf8Character(character) ?? Missing),
            ];
        }

        (string Name, Reading Read)[] readings = size switch
        {
            2 =>
            [
                ("uint16", (b, o) => ByteReading.Integer(ByteReading.UInt16(b, o))),
                ("int16", (b, o) => ByteReading.Integer(ByteReading.Int16(b, o))),
                ("half", (b, o) => ByteReading.Float(ByteReading.Half(b, o))),
            ],
            4 =>
            [
                ("uint32", (b, o) => ByteReading.Integer(ByteReading.UInt32(b, o))),
                ("int32", (b, o) => ByteReading.Integer(ByteReading.Int32(b, o))),
                ("float", (b, o) => ByteReading.Float(ByteReading.Single(b, o))),
                ("Unix s", (b, o) => ByteReading.UnixSeconds(ByteReading.UInt32(b, o)) ?? Missing),
            ],
            _ =>
            [
                ("uint64", (b, o) => ByteReading.Integer(ByteReading.UInt64(b, o))),
                ("int64", (b, o) => ByteReading.Integer(ByteReading.Int64(b, o))),
                ("double", (b, o) => ByteReading.Float(ByteReading.Double(b, o))),
                ("Unix s", (b, o) => ByteReading.UnixSeconds(ByteReading.Int64(b, o)) ?? Missing),
                ("Unix ms", (b, o) => ByteReading.UnixMilliseconds(ByteReading.Int64(b, o)) ?? Missing),
            ],
        };

        bool whole = bytes.Length >= size;
        var rows = new List<HexValueRow>(readings.Length);
        foreach (var (name, read) in readings)
            rows.Add(new(name, size, whole ? read(bytes[..size], order) : Missing));
        return rows;
    }

    private delegate string Reading(ReadOnlySpan<byte> bytes, ByteOrder order);

    private static IReadOnlyList<string> Bits(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>(2);
        for (int at = 0; at < bytes.Length; at += 4)
        {
            var line = new System.Text.StringBuilder();
            for (int i = at; i < Math.Min(at + 4, bytes.Length); i++)
            {
                string bits = Convert.ToString(bytes[i], 2).PadLeft(8, '0');
                if (i > at)
                    line.Append("  ");
                line.Append(bits, 0, 4).Append(' ').Append(bits, 4, 4);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }
}
