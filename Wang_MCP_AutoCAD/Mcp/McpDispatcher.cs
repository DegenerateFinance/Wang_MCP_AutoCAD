using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The result of dispatching one request body: an HTTP status and, unless the request was a
/// notification, the JSON-RPC response to write back.
///
/// <see cref="RpcErrorCode"/> and <see cref="IsToolError"/> are deliberately separate. A
/// JSON-RPC error means the client is broken; a tool error means a well-formed call did not
/// work and the model should read the message and retry. Both are failures worth logging,
/// but only the first is a protocol fault.
/// </summary>
public readonly record struct DispatchResult(
    int HttpStatus,
    string? ResponseJson,
    string MethodName,
    string ToolName,
    bool IsToolError,
    int? RpcErrorCode = null)
{
    public bool IsFailure => IsToolError || RpcErrorCode is not null;

    public string Outcome
    {
        get
        {
            if (RpcErrorCode is not null)
            {
                return $"rpc_error({RpcErrorCode})";
            }

            return IsToolError ? "tool_error" : "ok";
        }
    }
}

/// <summary>
/// JSON-RPC method routing. Pure string in, result out — no sockets, no AutoCAD — which is
/// what makes this the most valuable type in the project to unit-test.
/// </summary>
public sealed class McpDispatcher
{
    private readonly ToolRegistry _registry;
    private readonly IDocumentGateway _gateway;
    private readonly McpServerOptions _options;

    public McpDispatcher(ToolRegistry registry, IDocumentGateway gateway, McpServerOptions options)
    {
        _registry = registry;
        _gateway = gateway;
        _options = options;
    }

    public DispatchResult Dispatch(string body)
    {
        if (!JsonRpcCodec.TryParse(body, out JsonRpcRequest? request, out string? errorJson))
        {
            // A malformed envelope is still a completed HTTP exchange; the failure lives
            // in the JSON-RPC error object, not the status line.
            return new DispatchResult(200, errorJson, "(unparsed)", "", false, JsonRpcCodec.ErrorCodeOf(errorJson!));
        }

        JsonRpcRequest parsed = request!;

        try
        {
            return Route(parsed);
        }
        catch (System.Exception ex)
        {
            Log.Error($"op=dispatch method={parsed.Method} outcome=internal_error", ex);
            if (parsed.IsNotification)
            {
                return new DispatchResult(202, null, parsed.Method, "", false);
            }

            return new DispatchResult(
                200,
                JsonRpcCodec.WriteError(parsed.Id, JsonRpcErrorCode.InternalError, "The server failed to handle the request; see the MCP log."),
                parsed.Method,
                "",
                false,
                JsonRpcErrorCode.InternalError);
        }
    }

    private DispatchResult Route(JsonRpcRequest request)
    {
        // Notifications never get a response body, whatever they are. Unknown ones are
        // ignored rather than rejected, as the spec requires.
        if (request.IsNotification)
        {
            Log.Debug($"op=dispatch method={request.Method} kind=notification");
            return new DispatchResult(202, null, request.Method, "", false);
        }

        switch (request.Method)
        {
            case "initialize":
                return Ok(request, BuildInitializeResult(request));

            case "ping":
                return Ok(request, new JsonObject());

            case "tools/list":
                return Ok(request, new JsonObject { ["tools"] = _registry.ToListArray() });

            case "tools/call":
                return CallTool(request);

            default:
                Log.Warn($"op=dispatch method={request.Method} outcome=method_not_found");
                return new DispatchResult(
                    200,
                    JsonRpcCodec.WriteError(request.Id, JsonRpcErrorCode.MethodNotFound, $"Unknown method \"{request.Method}\"."),
                    request.Method,
                    "",
                    false,
                    JsonRpcErrorCode.MethodNotFound);
        }
    }

    private JsonObject BuildInitializeResult(JsonRpcRequest request)
    {
        // Echo the client's protocol version when we speak it, otherwise state ours and let
        // the client decide whether it can proceed.
        string negotiated = _options.ProtocolVersion;
        if (request.Params is JsonElement parameters
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("protocolVersion", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String)
        {
            string? requested = versionElement.GetString();
            if (!string.IsNullOrEmpty(requested) && requested == _options.ProtocolVersion)
            {
                negotiated = requested;
            }
        }

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _options.ServerName,
                ["version"] = _options.ServerVersion,
            },
            ["instructions"] =
                "Drives a running AutoCAD 2026 session in-process. All lengths in arguments and "
                + "responses are millimetres (mm_ prefix); the server converts to the drawing's "
                + "own units using its INSUNITS setting and echoes mm_per_drawing_unit so the "
                + "conversion is auditable.",
        };
    }

    private DispatchResult CallTool(JsonRpcRequest request)
    {
        if (request.Params is not JsonElement parameters || parameters.ValueKind != JsonValueKind.Object)
        {
            return InvalidParams(request, "tools/call requires a params object with a \"name\".");
        }

        if (!parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return InvalidParams(request, "tools/call requires a string \"name\" naming the tool.");
        }

        string toolName = nameElement.GetString() ?? "";

        if (!_registry.TryGet(toolName, out McpTool? tool))
        {
            // Unregistered tool is a client bug — no argument fix helps — so it is a
            // JSON-RPC error, unlike a bad argument value which comes back as isError.
            Log.Warn($"op=tools/call tool={toolName} outcome=unknown_tool");
            return InvalidParams(request, $"Unknown tool \"{toolName}\".");
        }

        JsonElement? argumentsElement = null;
        if (parameters.TryGetProperty("arguments", out JsonElement argsElement))
        {
            argumentsElement = argsElement;
        }

        ToolArgs args = new(argumentsElement);
        McpTool resolved = tool!;

        Stopwatch sw = Stopwatch.StartNew();
        ToolResult result = Invoke(resolved, args);
        sw.Stop();

        Log.Write(
            result.IsError ? LogLevel.Warn : LogLevel.Info,
            $"op=tools/call tool={toolName} outcome={(result.IsError ? "tool_error" : "ok")} elapsed_ms={sw.ElapsedMilliseconds}");

        return new DispatchResult(
            200,
            JsonRpcCodec.WriteResult(request.Id, result.ToResultObject()),
            request.Method,
            toolName,
            result.IsError);
    }

    private ToolResult Invoke(McpTool tool, ToolArgs args)
    {
        if (!tool.RequiresDocument)
        {
            try
            {
                return tool.Handler(args);
            }
            catch (System.Exception ex)
            {
                Log.Error($"op=tools/call tool={tool.Name} outcome=handler_threw", ex);
                return ToolResult.Fail($"The tool \"{tool.Name}\" failed: {ex.Message}");
            }
        }

        if (!_gateway.TryRun(
                work: () => tool.Handler(args),
                ms_Timeout: _options.Ms_DocumentCallTimeout,
                outcome: out ToolResult? outcome,
                failureReason: out string failureReason))
        {
            return ToolResult.Fail(failureReason);
        }

        return outcome ?? ToolResult.Fail($"The tool \"{tool.Name}\" returned no result.");
    }

    private static DispatchResult Ok(JsonRpcRequest request, JsonObject result)
    {
        return new DispatchResult(200, JsonRpcCodec.WriteResult(request.Id, result), request.Method, "", false);
    }

    private static DispatchResult InvalidParams(JsonRpcRequest request, string message)
    {
        return new DispatchResult(
            200,
            JsonRpcCodec.WriteError(request.Id, JsonRpcErrorCode.InvalidParams, message),
            request.Method,
            "",
            false,
            JsonRpcErrorCode.InvalidParams);
    }
}
