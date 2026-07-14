namespace JsonViewerCore.Features.Json;

public sealed class JsonNode
{
    public int Depth { get; init; }
    public string Text { get; init; } = "";
}