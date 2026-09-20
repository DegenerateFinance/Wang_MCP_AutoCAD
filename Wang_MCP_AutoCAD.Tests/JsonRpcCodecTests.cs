using System.Text.Json;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Tests;

[Collection(LogGlobalCollection.Name)]
public class JsonRpcCodecTests
{
    [Fact]
    public void Parse_Garbage_ReturnsParseError()
    {
        bool parsed = JsonRpcCodec.TryParse("{not json", out JsonRpcRequest? request, out string? errorJson);

        Assert.False(parsed);
        Assert.Null(request);
        Assert.Equal(JsonRpcErrorCode.ParseError, ErrorCodeOf(errorJson!));
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsInvalidRequest()
    {
        bool parsed = JsonRpcCodec.TryParse("   ", out JsonRpcRequest? _, out string? errorJson);

        Assert.False(parsed);
        Assert.Equal(JsonRpcErrorCode.InvalidRequest, ErrorCodeOf(errorJson!));
    }

    [Fact]
    public void Parse_JsonArray_ReturnsInvalidRequest_BecauseBatchingIsUnsupported()
    {
        bool parsed = JsonRpcCodec.TryParse("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", out JsonRpcRequest? _, out string? errorJson);

        Assert.False(parsed);
        Assert.Equal(JsonRpcErrorCode.InvalidRequest, ErrorCodeOf(errorJson!));
        Assert.Contains("Batch", MessageOf(errorJson!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WrongJsonRpcVersion_ReturnsInvalidRequest()
    {
        bool parsed = JsonRpcCodec.TryParse("""{"jsonrpc":"1.0","id":1,"method":"ping"}""", out JsonRpcRequest? _, out string? errorJson);

        Assert.False(parsed);
        Assert.Equal(JsonRpcErrorCode.InvalidRequest, ErrorCodeOf(errorJson!));
    }

    [Fact]
    public void Parse_MissingMethod_ReturnsInvalidRequest()
    {
        bool parsed = JsonRpcCodec.TryParse("""{"jsonrpc":"2.0","id":1}""", out JsonRpcRequest? _, out string? errorJson);

        Assert.False(parsed);
        Assert.Equal(JsonRpcErrorCode.InvalidRequest, ErrorCodeOf(errorJson!));
    }

    [Fact]
    public void Parse_NoId_IsNotification()
    {
        bool parsed = JsonRpcCodec.TryParse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", out JsonRpcRequest? request, out string? _);

        Assert.True(parsed);
        Assert.True(request!.IsNotification);
    }

    [Fact]
    public void Parse_NullId_IsNotANotification()
    {
        // An explicit null id is still a request that must be answered, echoing null back.
        bool parsed = JsonRpcCodec.TryParse("""{"jsonrpc":"2.0","id":null,"method":"ping"}""", out JsonRpcRequest? request, out string? _);

        Assert.True(parsed);
        Assert.False(request!.IsNotification);
        Assert.Null(request.Id);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", "7")]
    [InlineData("""{"jsonrpc":"2.0","id":"abc","method":"ping"}""", "\"abc\"")]
    public void Parse_IdKindsRoundTripThroughTheResponse(string body, string expectedIdJson)
    {
        JsonRpcCodec.TryParse(body, out JsonRpcRequest? request, out string? _);

        string response = JsonRpcCodec.WriteResult(request!.Id, new JsonObject());

        JsonNode node = JsonNode.Parse(response)!;
        Assert.Equal(expectedIdJson, node["id"]!.ToJsonString());
    }

    [Fact]
    public void Parse_KeepsParamsUsableAfterTheBackingDocumentIsDisposed()
    {
        // Regression guard: params is cloned out of the JsonDocument, which TryParse disposes.
        JsonRpcCodec.TryParse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"acad_ping"}}""",
            out JsonRpcRequest? request,
            out string? _);

        JsonElement parameters = request!.Params!.Value;
        Assert.True(parameters.TryGetProperty("name", out JsonElement name));
        Assert.Equal("acad_ping", name.GetString());
    }

    [Fact]
    public void WriteError_EchoesIdAndCarriesCodeAndMessage()
    {
        string json = JsonRpcCodec.WriteError(JsonValue.Create(42), JsonRpcErrorCode.MethodNotFound, "nope");

        JsonNode node = JsonNode.Parse(json)!;
        Assert.Equal("2.0", node["jsonrpc"]!.GetValue<string>());
        Assert.Equal(42, node["id"]!.GetValue<int>());
        Assert.Equal(JsonRpcErrorCode.MethodNotFound, node["error"]!["code"]!.GetValue<int>());
        Assert.Equal("nope", node["error"]!["message"]!.GetValue<string>());
    }

    private static int ErrorCodeOf(string json) => JsonNode.Parse(json)!["error"]!["code"]!.GetValue<int>();

    private static string MessageOf(string json) => JsonNode.Parse(json)!["error"]!["message"]!.GetValue<string>();
}
