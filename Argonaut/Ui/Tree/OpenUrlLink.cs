using System;

namespace Argonaut.Ui.Tree;

/// <summary>Open a web or mail address the value holds, in the system's handler for it.</summary>
/// <param name="Address">An absolute <c>http</c>, <c>https</c> or <c>mailto</c> URI - the only
/// schemes a value may hand to the system.</param>
public sealed record OpenUrlLink(Uri Address);
