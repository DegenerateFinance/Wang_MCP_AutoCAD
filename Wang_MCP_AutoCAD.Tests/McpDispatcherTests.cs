using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Tests;

[Collection(LogGlobalCollection.Name)]
public class McpDispatcherTests
{
    private readonly McpServerOptions _options = new();
    private readonly FakeDocumentGateway _gateway = new();
    private readonly ToolRegistry _registry = new();

    public McpDispatcherTests()
    {
        _registry.Add(PingTool.Create(_options, () => new ServerStats(0, 0, 0, 0)));
    }

    private McpDispatcher Dispatcher() => new(_registry, _gateway, _options);

    [Fact]
    public void Initialize_ReturnsProtocolVersionCapabilitiesAndServerInfo()
    {
        DispatchResult result = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");

        JsonNode node = JsonNode.Parse(result.ResponseJson!)!;
        JsonNode payload = node["result"]!;

        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("2025-06-18", payload["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(payload["capabilities"]!["tools"]);
        Assert.Equal(_options.ServerName, payload["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_WithNoParams_StillSucceeds()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        JsonNode node = JsonNode.Parse(result.ResponseJson!)!;
        Assert.Equal(_options.ProtocolVersion, node["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void InitializedNotification_ProducesNoResponseBodyAnd202()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(202, result.HttpStatus);
        Assert.Null(result.ResponseJson);
    }

    [Fact]
    public void UnknownNotification_IsIgnoredRatherThanRejected()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","method":"notifications/somethingNew"}""");

        Assert.Equal(202, result.HttpStatus);
        Assert.Null(result.ResponseJson);
    }

    [Fact]
    public void Ping_ReturnsEmptyResult()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        JsonNode node = JsonNode.Parse(result.ResponseJson!)!;
        Assert.NotNull(node["result"]);
        Assert.Null(node["error"]);
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}""");

        JsonNode node = JsonNode.Parse(result.ResponseJson!)!;
        Assert.Equal(JsonRpcErrorCode.MethodNotFound, node["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_ContainsRegisteredToolsWithObjectSchemas()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":5,"method":"tools/list"}""");

        JsonArray tools = JsonNode.Parse(result.ResponseJson!)!["result"]!["tools"]!.AsArray();

        Assert.NotEmpty(tools);
        foreach (JsonNode? tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
        }

        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "acad_ping");
    }

    [Fact]
    public void ToolsCall_UnknownTool_ReturnsInvalidParamsNotIsError()
    {
        // A tool that does not exist is a client bug: no argument fix helps, so it is a
        // JSON-RPC error rather than isError content.
        DispatchResult result = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"acad_nope","arguments":{}}}""");

        JsonNode node = JsonNode.Parse(result.ResponseJson!)!;
        Assert.Equal(JsonRpcErrorCode.InvalidParams, node["error"]!["code"]!.GetValue<int>());
        Assert.Null(node["result"]);
    }

    [Fact]
    public void ToolsCall_MissingNameParam_ReturnsInvalidParams()
    {
        DispatchResult result = Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""");

        JsonNode node = JsonNode.Parse(result.ResponseJson!)!;
        Assert.Equal(JsonRpcErrorCode.InvalidParams, node["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Ping_ReturnsParsableJsonInTheContentText()
    {
        DispatchResult result = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"acad_ping","arguments":{}}}""");

        JsonNode payload = JsonNode.Parse(result.ResponseJson!)!["result"]!;

        Assert.False(payload["isError"]!.GetValue<bool>());
        string text = payload["content"]!.AsArray()[0]!["text"]!.GetValue<string>();

        JsonNode inner = JsonNode.Parse(text)!;
        Assert.Equal("wang-mcp-autocad", inner["server"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_NonDocumentTool_NeverTouchesTheGateway()
    {
        Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"acad_ping","arguments":{}}}""");

        Assert.Equal(0, _gateway.RunCount);
    }

    [Fact]
    public void ToolsCall_ThrowingHandler_ReturnsIsErrorNotAnUnhandledEscape()
    {
        _registry.Add(new McpTool
        {
            Name = "boom",
            Description = "throws",
            InputSchema = ToolSchema.Object().Build(),
            RequiresDocument = false,
            Handler = _ => throw new InvalidOperationException("kaboom"),
        });

        DispatchResult result = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"boom","arguments":{}}}""");

        JsonNode payload = JsonNode.Parse(result.ResponseJson!)!["result"]!;
        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("kaboom", payload["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_GatewayUnavailable_ReturnsIsErrorCarryingTheReason()
    {
        _registry.Add(new McpTool
        {
            Name = "needs_doc",
            Description = "needs the document thread",
            InputSchema = ToolSchema.Object().Build(),
            RequiresDocument = true,
            Handler = _ => ToolResult.Ok("should not be reached"),
        });

        _gateway.ShouldFail = true;
        _gateway.FailureReason = "AutoCAD did not become available within 15000 ms.";

        DispatchResult result = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"needs_doc","arguments":{}}}""");

        JsonNode payload = JsonNode.Parse(result.ResponseJson!)!["result"]!;
        Assert.True(payload["isError"]!.GetValue<bool>());
        Assert.Contains("15000", payload["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_DocumentTool_IsMarshalledThroughTheGateway()
    {
        _registry.Add(new McpTool
        {
            Name = "needs_doc",
            Description = "needs the document thread",
            InputSchema = ToolSchema.Object().Build(),
            RequiresDocument = true,
            Handler = _ => ToolResult.Ok("done"),
        });

        Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"needs_doc","arguments":{}}}""");

        Assert.Equal(1, _gateway.RunCount);
    }

    [Fact]
    public void RpcErrorsAreClassifiedAsFailures_SoTheHttpLineLogsAtError()
    {
        DispatchResult unknownMethod = Dispatcher().Dispatch("""{"jsonrpc":"2.0","id":13,"method":"nope"}""");
        DispatchResult unknownTool = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"nope"}}""");
        DispatchResult malformed = Dispatcher().Dispatch("}{");

        Assert.True(unknownMethod.IsFailure);
        Assert.Equal(JsonRpcErrorCode.MethodNotFound, unknownMethod.RpcErrorCode);

        Assert.True(unknownTool.IsFailure);
        Assert.Equal(JsonRpcErrorCode.InvalidParams, unknownTool.RpcErrorCode);

        Assert.True(malformed.IsFailure);
        Assert.Equal(JsonRpcErrorCode.ParseError, malformed.RpcErrorCode);
    }

    [Fact]
    public void SuccessAndToolErrorAreDistinguishedFromProtocolErrors()
    {
        DispatchResult success = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"acad_ping","arguments":{}}}""");

        Assert.False(success.IsFailure);
        Assert.Null(success.RpcErrorCode);
        Assert.Equal("ok", success.Outcome);

        _registry.Add(new McpTool
        {
            Name = "sad",
            Description = "fails",
            InputSchema = ToolSchema.Object().Build(),
            RequiresDocument = false,
            Handler = _ => ToolResult.Fail("bad argument"),
        });

        DispatchResult toolError = Dispatcher().Dispatch(
            """{"jsonrpc":"2.0","id":16,"method":"tools/call","params":{"name":"sad","arguments":{}}}""");

        // A tool failure is a failure for logging, but carries no JSON-RPC error code:
        // the model is meant to read the message and retry.
        Assert.True(toolError.IsFailure);
        Assert.Null(toolError.RpcErrorCode);
        Assert.Equal("tool_error", toolError.Outcome);
    }

    [Fact]
    public void MalformedBody_ReturnsParseErrorAsA200()
    {
        // A JSON-RPC error is still a successful HTTP exchange.
        DispatchResult result = Dispatcher().Dispatch("}{");

        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(JsonRpcErrorCode.ParseError, JsonNode.Parse(result.ResponseJson!)!["error"]!["code"]!.GetValue<int>());
    }
}
