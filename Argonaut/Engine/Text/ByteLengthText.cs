namespace Argonaut.Engine.Text;

/// <summary>A length in bytes as a person reads it: "812 bytes", "4.2 KB", "3 MB".</summary>
public static class ByteLengthText
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.#} GB",
    };
}
