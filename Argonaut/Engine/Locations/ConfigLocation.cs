namespace Argonaut.Engine.Locations;

/// <summary>A folder where machine and user configuration files usually live, with the name the
/// shell shows for it.</summary>
public sealed record ConfigLocation(string Name, string Path);
