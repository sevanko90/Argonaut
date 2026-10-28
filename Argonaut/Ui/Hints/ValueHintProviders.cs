using System;
using System.Collections.Generic;
using Argonaut.Engine.Hints;

namespace Argonaut.Ui.Hints;

/// <summary>The hint providers every tree view classifies its text with, in the order they are
/// asked: the first to recognise a value wins.</summary>
public static class ValueHintProviders
{
    public static IReadOnlyList<IValueHintProvider> Standard(DateHintSettings settings, TimeProvider time)
        =>
        [
            new DateHintProvider(settings), new IsoDateHintProvider(settings, time), new ColourHintProvider(),
            new JwtHintProvider(time), new UrlHintProvider(), new CronHintProvider(settings, time),
            new EmbeddedJsonHintProvider(), new Base64HintProvider(),
        ];
}
