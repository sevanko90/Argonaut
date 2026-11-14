using System.Text.Json;
using Argonaut.Features.Json;

namespace Argonaut.Tests.Features.Json;

/// <summary>The JSON view's settings block as read from a settings file someone may have written
/// before a setting existed.</summary>
public sealed class JsonViewSettingsTests
{
    [Fact]
    public void ASettingsFileThatLeavesSettingsOut_GetsTheirDefaults()
    {
        var read = JsonSerializer.Deserialize("{}", JsonViewSettings.JsonTypeInfo)!;

        Assert.True(read.OpenMatchedSchemas);
        Assert.Equal(JsonViewSettings.DefaultExpandDepth, read.ExpandDepth);
    }

    [Fact]
    public void OpenMatchedSchemasTurnedOff_RoundTrips()
    {
        string json = JsonSerializer.Serialize(new JsonViewSettings { OpenMatchedSchemas = false }, JsonViewSettings.JsonTypeInfo);

        Assert.False(JsonSerializer.Deserialize(json, JsonViewSettings.JsonTypeInfo)!.OpenMatchedSchemas);
    }
}
