namespace Argonaut.Tests;

/// <summary>
/// Groups every test class that mutates the static <c>AppDataPaths.RootOverride</c> test seam
/// into one xUnit collection, so xUnit's default cross-collection parallelism can't run two of
/// them at once and race on that shared field.
/// </summary>
[CollectionDefinition("AppDataPaths")]
public sealed class AppDataPathsCollection;
