namespace Argonaut.Engine.Settings;

/// <summary>
/// The app's settings, one shared instance per <see cref="ISettingsBlock{TSelf}"/> type. Only
/// the composition root holds this; everything below it is handed the blocks it uses.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// The block of type <typeparamref name="T"/>, read from what is stored the first time it is
    /// asked for and the same instance every time after, so every holder sees every change.
    /// </summary>
    T Get<T>() where T : class, ISettingsBlock<T>, new();

    /// <summary>Writes every block out. Call at shutdown, and before anything that ends the
    /// process without one (an update restart). Nothing watches the blocks, so a change is only
    /// persisted by this.</summary>
    void Save();
}
