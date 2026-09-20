using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>JSON-RPC 2.0 error codes. Protocol-level failures only — see <see cref="ToolResult"/>.</summary>
public static class JsonRpcErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>
/// A parsed JSON-RPC request. <see cref="Id"/> is kept as a <see cref="JsonNode"/> because the
/// spec allows a string, a number or null, and the response must echo it back unchanged.
/// A request with no id at all is a notification and gets no response.
/// </summary>
public sealed class JsonRpcRequest
{
    public required string Method { get; init; }

    public JsonNode? Id { get; init; }

    public JsonElement? Params { get; init; }

    public bool IsNotification { get; init; }
}

/// <summary>Parses and writes JSON-RPC envelopes. Pure string in, string out.</summary>
public static class JsonRpcCodec
{
    /// <summary>
    /// Parses a request body. Returns false and fills <paramref name="errorJson"/> with a
    /// complete JSON-RPC error response on any envelope problem. Never throws.
    /// </summary>
    public static bool TryParse(string body, out JsonRpcRequest? request, out string? errorJson)
    {
        request = null;
        errorJson = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            errorJson = WriteError(null, JsonRpcErrorCode.InvalidRequest, "Request body was empty.");
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // A client sending bad JSON is expected, not exceptional, and the stack trace is
            // the same ten System.Text.Json frames every time. Keep the Warn line greppable
            // and put the detail one level down for when it is actually being diagnosed.
            Log.Warn("op=jsonrpc/parse outcome=malformed_json");
            Log.Write(LogLevel.Debug, "op=jsonrpc/parse outcome=malformed_json detail=exception", ex);
            errorJson = WriteError(null, JsonRpcErrorCode.ParseError, "Request body was not valid JSON.");
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                errorJson = WriteError(
                    null,
                    JsonRpcErrorCode.InvalidRequest,
                    "Batch requests are not supported; send one JSON-RPC object per request.");
                return false;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                errorJson = WriteError(null, JsonRpcErrorCode.InvalidRequest, "Request body was not a JSON object.");
                return false;
            }

            JsonNode? id = null;
            bool hasId = false;
            if (root.TryGetProperty("id", out JsonElement idElement))
            {
                hasId = true;
                id = idElement.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(idElement.GetRawText());
            }

            if (!root.TryGetProperty("jsonrpc", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || versionElement.GetString() != "2.0")
            {
                errorJson = WriteError(id, JsonRpcErrorCode.InvalidRequest, "Member \"jsonrpc\" must be the string \"2.0\".");
                return false;
            }

            if (!root.TryGetProperty("method", out JsonElement methodElement)
                || methodElement.ValueKind != JsonValueKind.String)
            {
                errorJson = WriteError(id, JsonRpcErrorCode.InvalidRequest, "Member \"method\" is required and must be a string.");
                return false;
            }

            string? method = methodElement.GetString();
            if (string.IsNullOrEmpty(method))
            {
                errorJson = WriteError(id, JsonRpcErrorCode.InvalidRequest, "Member \"method\" must not be empty.");
                return false;
            }

            JsonElement? parameters = null;
            if (root.TryGetProperty("params", out JsonElement paramsElement))
            {
                // Clone: the JsonDocument backing this element is disposed on the way out.
                parameters = paramsElement.Clone();
            }

            request = new JsonRpcRequest
            {
                Method = method,
                Id = id,
                Params = parameters,
                IsNotification = !hasId,
            };

            return true;
        }
    }

    /// <summary>Writes a success response echoing <paramref name="id"/> with the given result object.</summary>
    public static string WriteResult(JsonNode? id, JsonNode result)
    {
        JsonObject envelope = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

        return envelope.ToJsonString();
    }

    /// <summary>
    /// Reads the code back out of an error response this class wrote, so the HTTP layer can
    /// log the outcome without re-deriving it. Returns null for anything that is not one.
    /// </summary>
    public static int? ErrorCodeOf(string responseJson)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(responseJson);
            return node?["error"]?["code"]?.GetValue<int>();
        }
        catch (System.Exception ex)
        {
            Log.Warn("op=jsonrpc/error_code outcome=unreadable", ex);
            return null;
        }
    }

    public static string WriteError(JsonNode? id, int code, string message)
    {
        JsonObject envelope = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        return envelope.ToJsonString();
    }
}
