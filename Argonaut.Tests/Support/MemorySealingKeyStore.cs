using Argonaut.Engine.Settings;

namespace Argonaut.Tests.Support;

/// <summary>A sealing key kept in memory, so sealer tests never touch the Keychain, DPAPI or the
/// config folder. <see cref="RefuseSaves"/> plays a store that cannot keep a key.</summary>
internal sealed class MemorySealingKeyStore : ISealingKeyStore
{
    public byte[]? Key { get; set; }

    public bool RefuseSaves { get; set; }

    public int Saves { get; private set; }

    public byte[]? LoadKey() => Key;

    public bool SaveKey(byte[] key)
    {
        if (RefuseSaves)
            return false;

        Saves++;
        Key = key;
        return true;
    }
}
