using System.Collections;
using Argonaut.Features.Raw;

namespace Argonaut.Tests;

/// <summary>
/// The growth notification's allocation-free placeholder list is handed straight to Avalonia
/// inside NotifyCollectionChangedEventArgs, so its IList surface must behave exactly like the
/// object?[] it replaces: right count, all-null reads, null-yielding enumeration and CopyTo.
/// </summary>
public class RawRowCollectionTests
{
    [Fact]
    public void NullPlaceholderList_BehavesLikeAnArrayOfNulls()
    {
        var list = new RawRowCollection.NullPlaceholderList(3);

        Assert.Equal(3, list.Count);
        Assert.Null(list[0]);
        Assert.Null(list[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[3]);

        int enumerated = 0;
        foreach (var item in (IEnumerable)list)
        {
            Assert.Null(item);
            enumerated++;
        }
        Assert.Equal(3, enumerated);

        var target = new object?[] { "sentinel", "sentinel", "sentinel", "sentinel" };
        list.CopyTo(target, 1);
        Assert.Equal("sentinel", target[0]);
        Assert.Null(target[1]);
        Assert.Null(target[3]);
    }
}
