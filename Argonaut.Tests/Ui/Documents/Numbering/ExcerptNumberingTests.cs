using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Engine.Settings;
using Argonaut.Features.Csv;
using Argonaut.Features.Raw;
using Argonaut.Ui.Documents.Numbering;

namespace Argonaut.Tests.Ui.Documents.Numbering;

/// <summary>
/// An excerpt shows its lines and offsets as the file numbers them, or counted from its own
/// start, by the one shared switch - and flipping it renumbers what is open.
/// </summary>
public class ExcerptNumberingTests
{
    private static ExcerptByteOrigin Excerpt(string text, long first, long? last, string name = "big.txt")
    {
        var whole = new MemoryByteOrigin(Encoding.UTF8.GetBytes(text), name);
        var source = whole.Open();
        try
        {
            return new ExcerptByteOrigin(whole, LineRangeLocator.Locate(source, first, last)!.Value);
        }
        finally
        {
            source.Release();
        }
    }

    private static LineNumberingPreference Preference(bool fromSource)
        => new(new LineNumberingSettings { NumberExcerptsFromSource = fromSource });

    [Fact]
    public void An_excerpt_is_numbered_by_the_file_or_from_its_start()
    {
        using var excerpt = Excerpt("a\nbb\nccc\n", 2, 3);

        Assert.Equal(new LineNumbering(2, 2), Preference(fromSource: true).For(excerpt));
        Assert.Equal(LineNumbering.FromStart, Preference(fromSource: false).For(excerpt));
    }

    [Fact]
    public void A_whole_document_is_always_numbered_from_its_start()
    {
        using var whole = new MemoryByteOrigin("a\n"u8.ToArray(), "a.txt");

        Assert.Equal(LineNumbering.FromStart, Preference(fromSource: true).For(whole));
    }

    [Fact]
    public void The_preference_is_remembered_in_its_settings_block()
    {
        var settings = SettingsStore.InMemory();
        var preference = new LineNumberingPreference(settings.Get<LineNumberingSettings>());

        preference.NumberExcerptsFromSource = false;

        Assert.False(settings.Get<LineNumberingSettings>().NumberExcerptsFromSource);
    }

    [Fact]
    public async Task The_raw_caret_readout_follows_the_switch()
    {
        var preference = Preference(fromSource: true);
        var vm = new RawViewModel(new RawViewSettings(), lineNumbers: preference);
        try
        {
            await vm.LoadAsync(Excerpt("abc\ndef\nghi\njkl\n", 3, 4));
            await vm.IndexingTask;

            vm.Caret!.PlaceAt(5);
            Assert.Equal("Byte 13    Ln 4, Col 2", vm.CaretPositionText);

            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            preference.NumberExcerptsFromSource = false;

            Assert.Equal("Byte 5    Ln 2, Col 2", vm.CaretPositionText);
            Assert.Contains(nameof(RawViewModel.CaretPositionText), changed);
            Assert.Contains(nameof(RawViewModel.LineNumbering), changed);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task A_disposed_document_stops_following_the_switch()
    {
        var preference = Preference(fromSource: true);
        var vm = new RawViewModel(new RawViewSettings(), lineNumbers: preference);
        await vm.LoadAsync(Excerpt("abc\ndef\n", 2, 2));
        await vm.IndexingTask;
        vm.Dispose();

        preference.NumberExcerptsFromSource = false;

        Assert.Equal(new LineNumbering(2, 4), vm.LineNumbering);
    }

    [Fact]
    public async Task A_csv_excerpt_takes_its_header_from_the_whole_file()
    {
        var vm = new CsvViewModel();
        try
        {
            await vm.LoadAsync(Excerpt("id,name\n1,one\n2,two\n3,three\n", 3, 4, "rows.csv"), (byte)',');
            await vm.IndexingTask;

            Assert.Equal(["id", "name"], vm.Structure.Columns.Select(c => c.Name));
            Assert.Equal(2, vm.RowCount);
            Assert.Equal("2", vm.Rows.Cast<Argonaut.Ui.TableGrid.TableRow>().First().Cells[0].Text);

            // Unticked: generic names, and still every line is data.
            vm.IsHeaderRow = false;
            Assert.Equal(["Column 1", "Column 2"], vm.Structure.Columns.Select(c => c.Name));
            Assert.Equal(2, vm.RowCount);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task A_csv_excerpt_from_line_one_keeps_its_own_header()
    {
        var vm = new CsvViewModel();
        try
        {
            await vm.LoadAsync(Excerpt("id,name\n1,one\n2,two\n", 1, 2, "rows.csv"), (byte)',');
            await vm.IndexingTask;

            Assert.Equal(["id", "name"], vm.Structure.Columns.Select(c => c.Name));
            Assert.Equal(1, vm.RowCount);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void A_byte_excerpt_follows_the_file_for_offsets_only()
    {
        var whole = new MemoryByteOrigin("a\nbb\nccc\n"u8.ToArray(), "big.txt");
        using var excerpt = ExcerptByteOrigin.OfBytes(whole, new ByteRange(5, 4));

        Assert.Equal(new LineNumbering(1, 5), Preference(fromSource: true).For(excerpt));
    }

    [Fact]
    public async Task A_csv_byte_excerpt_takes_its_header_from_the_whole_file()
    {
        var whole = new MemoryByteOrigin("id,name\n1,one\n2,two\n"u8.ToArray(), "rows.csv");
        var vm = new CsvViewModel();
        try
        {
            await vm.LoadAsync(ExcerptByteOrigin.OfBytes(whole, new ByteRange(14, 6)), (byte)',');
            await vm.IndexingTask;

            Assert.Equal(["id", "name"], vm.Structure.Columns.Select(c => c.Name));
            Assert.Equal(1, vm.RowCount);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
