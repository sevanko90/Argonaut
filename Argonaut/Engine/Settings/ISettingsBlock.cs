using System.Text.Json.Serialization.Metadata;

namespace Argonaut.Engine.Settings;

/// <summary>
/// One section of the settings file, declared by whoever owns it - Raw owns its wrap width, the
/// Shell its theme - so the store never names a feature type.
///
/// A block is a plain class with settable properties and a parameterless constructor whose
/// field initialisers are the defaults. The store hands out one shared instance per type,
/// consumers read and assign its properties, and the store writes them all out once, at
/// shutdown. Setters are where values are brought back into range: the source-generated
/// deserialiser goes through them too, so a hand-edited file is normalised on the way in.
///
/// Everything else the store needs comes through static members: the key the block is filed
/// under, and a source-generated <see cref="JsonTypeInfo{T}"/>. The latter is what keeps
/// settings free of reflection-based serialisation, so they survive trimming and AOT without a
/// central registry of every block.
///
/// Blocks are UI-thread objects. Background work that needs a value takes a copy of it first.
/// </summary>
public interface ISettingsBlock<TSelf> where TSelf : class, ISettingsBlock<TSelf>, new()
{
    /// <summary>The block's property name in the settings file. Unique across the app, and kept
    /// stable - renaming it forgets what users had saved.</summary>
    static abstract string Key { get; }

    /// <summary>Source-generated metadata from the owner's own <c>JsonSerializerContext</c>.</summary>
    static abstract JsonTypeInfo<TSelf> JsonTypeInfo { get; }
}
