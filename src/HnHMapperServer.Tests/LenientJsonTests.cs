using System.Text.Json;
using HnHMapperServer.Core.Json;

namespace HnHMapperServer.Tests;

/// <summary>
/// Unit tests for LenientJson — repair of client marker payloads where shared-marker ids
/// arrive as bare hex tokens (UID.toString() written verbatim by the client's org.json).
/// </summary>
public class LenientJsonTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static List<Dictionary<string, object>>? Parse(string json) =>
        JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json, Options);

    [Fact]
    public void Repairs_HurricaneStyleBareHexIdPayload()
    {
        // Two player markers (no id), then the first shared marker with a bare hex oid —
        // the exact shape that made /markerUpdate die at $[2].id in production.
        const string payload =
            "[{\"name\":\"Home\",\"gridID\":\"1234567890123456789\",\"x\":50,\"y\":50,\"type\":\"player\",\"color\":\"java.awt.Color[r=255,g=255,b=255]\"}," +
            "{\"name\":\"Swamp\",\"gridID\":\"1234567890123456789\",\"x\":12,\"y\":93,\"type\":\"player\",\"color\":\"java.awt.Color[r=0,g=74,b=208]\"}," +
            "{\"name\":\"Deep cave\",\"gridID\":\"9876543210987654321\",\"x\":10,\"y\":91,\"type\":\"shared\",\"id\":531a9f2e44c81b07,\"image\":\"gfx/terobjs/mm/cave\"}]";

        Assert.Throws<JsonException>(() => Parse(payload));

        var repaired = LenientJson.QuoteBareTokens(payload);
        var markers = Parse(repaired);

        Assert.NotNull(markers);
        Assert.Equal(3, markers!.Count);

        var shared = markers[2];
        Assert.Equal("531a9f2e44c81b07", ((JsonElement)shared["id"]).GetString());
        Assert.Equal("Deep cave", ((JsonElement)shared["name"]).GetString());
        Assert.Equal("9876543210987654321", ((JsonElement)shared["gridID"]).GetString());
        Assert.Equal(JsonValueKind.Number, ((JsonElement)shared["x"]).ValueKind);
        Assert.Equal(10, ((JsonElement)shared["x"]).GetInt32());
        Assert.Equal(91, ((JsonElement)shared["y"]).GetInt32());
        Assert.Equal("gfx/terobjs/mm/cave", ((JsonElement)shared["image"]).GetString());

        Assert.Equal("Home", ((JsonElement)markers[0]["name"]).GetString());
        Assert.Equal(50, ((JsonElement)markers[0]["x"]).GetInt32());
    }

    [Fact]
    public void ValidJson_ReturnsSameInstance()
    {
        const string payload = "[{\"name\":\"A\",\"gridID\":\"42\",\"x\":1,\"y\":2,\"ready\":true}]";

        Assert.Same(payload, LenientJson.QuoteBareTokens(payload));
    }

    [Fact]
    public void ValidNumbers_StayBare()
    {
        const string payload = "[{\"a\":-12,\"b\":3.5,\"c\":1.2e-3,\"d\":0,\"e\":6E+2}]";

        Assert.Same(payload, LenientJson.QuoteBareTokens(payload));
    }

    [Fact]
    public void TrueFalseNull_StayBare()
    {
        const string payload = "[{\"a\":true,\"b\":false,\"c\":null}]";

        Assert.Same(payload, LenientJson.QuoteBareTokens(payload));
    }

    [Fact]
    public void LeadingZeroNumber_GetsQuoted()
    {
        var repaired = LenientJson.QuoteBareTokens("[{\"id\":0531}]");

        Assert.Equal("[{\"id\":\"0531\"}]", repaired);
        Assert.Equal("0531", ((JsonElement)Parse(repaired)![0]["id"]).GetString());
    }

    [Fact]
    public void BareTokenStartingWithLetter_GetsQuoted()
    {
        var repaired = LenientJson.QuoteBareTokens("[{\"id\":a531f9}]");

        Assert.Equal("a531f9", ((JsonElement)Parse(repaired)![0]["id"]).GetString());
    }

    [Fact]
    public void WhitespaceAroundBareToken_Preserved()
    {
        var repaired = LenientJson.QuoteBareTokens("[ { \"id\" : 12ab , \"x\" : 5 } ]");

        Assert.Equal("[ { \"id\" : \"12ab\" , \"x\" : 5 } ]", repaired);
    }

    [Fact]
    public void StringContents_NeverTouched()
    {
        // The name contains text that looks like a bare-token id assignment.
        const string payload = "[{\"name\":\"fake \\\"id\\\": 12ab, yes\",\"x\":1}]";

        Assert.Same(payload, LenientJson.QuoteBareTokens(payload));
        Assert.Equal("fake \"id\": 12ab, yes", ((JsonElement)Parse(payload)![0]["name"]).GetString());
    }

    [Fact]
    public void EscapedBackslashAtEndOfString_DoesNotDerailStringTracking()
    {
        var repaired = LenientJson.QuoteBareTokens("[{\"n\":\"c:\\\\\",\"id\":12ab}]");

        var markers = Parse(repaired);
        Assert.Equal("c:\\", ((JsonElement)markers![0]["n"]).GetString());
        Assert.Equal("12ab", ((JsonElement)markers[0]["id"]).GetString());
    }

    [Fact]
    public void MultipleBareTokens_AllRepaired()
    {
        var repaired = LenientJson.QuoteBareTokens("[{\"id\":12ab},{\"id\":f00d},{\"id\":77}]");

        var markers = Parse(repaired);
        Assert.Equal("12ab", ((JsonElement)markers![0]["id"]).GetString());
        Assert.Equal("f00d", ((JsonElement)markers[1]["id"]).GetString());
        Assert.Equal(77, ((JsonElement)markers[2]["id"]).GetInt32());
    }

    [Fact]
    public void StructurallyBrokenJson_IsLeftForTheParserToReject()
    {
        const string payload = "[{{\"x\":1}";

        Assert.Same(payload, LenientJson.QuoteBareTokens(payload));
        Assert.ThrowsAny<JsonException>(() => Parse(payload));
    }
}
