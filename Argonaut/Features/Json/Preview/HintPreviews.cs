using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Argonaut.Engine.Hints;
using Argonaut.Engine.Text;
using Argonaut.Ui.Hints;

namespace Argonaut.Features.Json.Preview;

/// <summary>
/// What a hint's value encodes, made ready for a hint card: decoded off the UI thread, then shown
/// as a tree, text, a picture or a note. Shared by every view whose hints open a card, from the
/// value's content bytes however the view came by them.
/// </summary>
public static class HintPreviews
{
    /// <summary>The card for a value too long to decode, saying so - null when it is not.</summary>
    public static HintPreview? TooLarge(long length)
        => length > HintExpansion.MaxValueBytes
            ? new HintPreview(ValuePreview.ForMessage("Too large to preview",
                $"This value is {ByteLengthText.Format(length)} - too large to decode here. Open it in the raw view to see it."), null)
            : null;

    /// <summary>
    /// Decodes <paramref name="raw"/> - the value's content as written - on a background thread
    /// and builds its preview, its JSON hinted by <paramref name="hintProviders"/> as the view's
    /// own values are. Null when it does not decode after all.
    /// </summary>
    public static async Task<HintPreview?> DecodeAsync(ValueHintKind kind, byte[] raw, IReadOnlyList<IValueHintProvider>? hintProviders,
        DateHintTimeZoneMode timeZone)
    {
        var decoded = await Task.Run(() => HintExpansion.Decode(kind, raw));
        if (decoded is null)
            return null;

        var preview = decoded.Kind switch
        {
            ExpandedKind.Json => ValuePreview.ForJson(decoded.Title, decoded.Bytes, kind == ValueHintKind.Jwt ? JwtHintProviders(hintProviders, timeZone) : hintProviders),
            ExpandedKind.Image => ValuePreview.ForImage(decoded.Title, decoded.Bytes),
            ExpandedKind.Text => ValuePreview.ForText(decoded.Title,
                Encoding.UTF8.GetString(decoded.Bytes, 0, Math.Min(decoded.Bytes.Length, ValuePreview.MaxTextBytes)),
                decoded.Bytes.Length > ValuePreview.MaxTextBytes),
            _ => ValuePreview.ForMessage(decoded.Title, "Binary data, with nothing here to show it. Open it as a document to see its bytes."),
        };
        return new HintPreview(preview, decoded);
    }

    /// <summary>The hints for a JWT's decoded claims: as the document's, except that a number
    /// reads as a date in seconds - RFC 7519 defines <c>exp</c>, <c>iat</c> and <c>nbf</c> that
    /// way - whatever the document's own numbers were taken to be.</summary>
    private static IReadOnlyList<IValueHintProvider> JwtHintProviders(IReadOnlyList<IValueHintProvider>? hintProviders, DateHintTimeZoneMode timeZone)
    {
        var seconds = new DateHintSettings();
        seconds.SetUserDefault(DateDecodingScheme.JsSeconds);
        seconds.SetTimeZoneMode(timeZone);
        var providers = new List<IValueHintProvider> { new DateHintProvider(seconds) };
        if (hintProviders is not null)
            providers.AddRange(hintProviders.Where(provider => provider is not DateHintProvider));
        return providers;
    }
}
