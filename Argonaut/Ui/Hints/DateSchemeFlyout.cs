using Argonaut.Engine.Hints;
using Avalonia.Controls;

namespace Argonaut.Ui.Hints;

/// <summary>
/// The menu a date hint's chip opens: how this one value is decoded - the file's default, or a
/// scheme of its own. One is built per view and reused; <see cref="Show"/> says whose value it
/// acts on.
/// </summary>
public sealed class DateSchemeFlyout
{
    private readonly MenuFlyout flyout = new();
    private DateHintSettings? settings;
    private long valueOffset = -1;

    public DateSchemeFlyout()
    {
        AddItem("File default", null);
        AddItem("Off", DateDecodingScheme.Off);
        AddItem("JS milliseconds", DateDecodingScheme.JsMilliseconds);
        AddItem("JS seconds", DateDecodingScheme.JsSeconds);
        AddItem("Keepa minutes", DateDecodingScheme.KeepaMinutes);
    }

    /// <summary>Opens the menu at the pointer over <paramref name="anchor"/>; the choice is stored
    /// in <paramref name="documentSettings"/> under the value at <paramref name="offset"/>.</summary>
    public void Show(Control anchor, DateHintSettings documentSettings, long offset)
    {
        settings = documentSettings;
        valueOffset = offset;
        flyout.ShowAt(anchor, showAtPointer: true);
    }

    private void AddItem(string header, DateDecodingScheme? scheme)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            if (settings is not null && valueOffset >= 0)
                settings.SetValueOverride(valueOffset, scheme);
        };
        flyout.Items.Add(item);
    }
}
