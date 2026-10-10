using System.Buffers.Text;
using System.Text;
using Argonaut.Features.Json.Hints;

namespace Argonaut.Tests.Features.Json.Hints;

public class HintExpansionTests
{
    private static ExpandedValue? Decode(ValueHintKind kind, string raw) => HintExpansion.Decode(kind, Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void AJwt_DecodesToItsHeaderAndClaims()
    {
        string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        var value = Decode(ValueHintKind.Jwt, $"{Part("""{"alg":"HS256"}""")}.{Part("""{"sub":"u1","exp":5}""")}.c2ln")!;
        Assert.Equal(ExpandedKind.Json, value.Kind);
        Assert.Equal("""{"header":{"alg":"HS256"},"claims":{"sub":"u1","exp":5}}""", Encoding.UTF8.GetString(value.Bytes));
    }

    [Fact]
    public void JsonInAString_IsUnescaped()
    {
        var value = Decode(ValueHintKind.EmbeddedJson, """{\"a\":[1,2],\"b\":\"x\\ny\"}""")!;
        Assert.Equal(ExpandedKind.Json, value.Kind);
        Assert.Equal("{\"a\":[1,2],\"b\":\"x\\ny\"}", Encoding.UTF8.GetString(value.Bytes));
    }

    [Fact]
    public void Base64_DecodesToAPictureTextOrBytes()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        Assert.Equal(ExpandedKind.Image, Decode(ValueHintKind.Base64, Convert.ToBase64String(png))!.Kind);
        Assert.Equal("PNG · 11 bytes", Decode(ValueHintKind.Base64, Convert.ToBase64String(png))!.Title);

        var text = Decode(ValueHintKind.Base64, Convert.ToBase64String(Encoding.UTF8.GetBytes("hello there, reader")))!;
        Assert.Equal((ExpandedKind.Text, "hello there, reader"), (text.Kind, Encoding.UTF8.GetString(text.Bytes)));

        Assert.Equal(ExpandedKind.Json, Decode(ValueHintKind.Base64, Convert.ToBase64String("""{"a":1}"""u8.ToArray()))!.Kind);
        Assert.Equal(ExpandedKind.Binary, Decode(ValueHintKind.Base64, Convert.ToBase64String([0x1F, 0x8B, 0x08, 0, 0xFF]))!.Kind);
    }

    [Fact]
    public void UnpaddedAndUrlSafeBase64_Decode()
    {
        string padded = Convert.ToBase64String(Encoding.UTF8.GetBytes("subjects?>>and~~more!"));
        Assert.Equal("subjects?>>and~~more!", Encoding.UTF8.GetString(Decode(ValueHintKind.Base64, padded.TrimEnd('='))!.Bytes));
        Assert.Equal("subjects?>>and~~more!", Encoding.UTF8.GetString(
            Decode(ValueHintKind.Base64, padded.TrimEnd('=').Replace('+', '-').Replace('/', '_'))!.Bytes));
    }

    [Fact]
    public void WhatDoesNotDecode_IsNull()
    {
        Assert.Null(Decode(ValueHintKind.Jwt, "abc.def"));
        Assert.Null(Decode(ValueHintKind.EmbeddedJson, """{\"a\":"""));
        Assert.Null(Decode(ValueHintKind.Base64, "Zm9v!YmFy"));
        Assert.Null(Decode(ValueHintKind.Colour, "#ffffff"));
    }
}
