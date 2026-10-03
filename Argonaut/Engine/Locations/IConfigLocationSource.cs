using System.Collections.Generic;

namespace Argonaut.Engine.Locations;

/// <summary>Where configuration files live on this machine, for the shell to offer as places to
/// start browsing from.</summary>
public interface IConfigLocationSource
{
    /// <summary>The configuration folders that exist right now. Asked each time the list is shown
    /// rather than once at startup, so a folder created or a drive mounted since is picked up.</summary>
    IReadOnlyList<ConfigLocation> GetExisting();
}
