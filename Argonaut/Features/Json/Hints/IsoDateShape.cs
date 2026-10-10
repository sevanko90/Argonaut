namespace Argonaut.Features.Json.Hints;

/// <summary>What an ISO 8601 string pins down: a calendar day, a wall-clock time with no zone, or
/// an instant, written with <c>Z</c> or an offset.</summary>
public enum IsoDateShape : byte
{
    Date,
    LocalDateTime,
    Instant,
}
