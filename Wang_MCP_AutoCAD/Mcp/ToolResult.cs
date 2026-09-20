using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The outcome of a tools/call. Note that a failure here is NOT a JSON-RPC error: it comes
/// back as HTTP 200 with "isError": true inside a successful result, because the model is
/// meant to read the message and correct itself. JSON-RPC error objects are reserved for
/// a broken client — malformed frame, unknown method, unregistered tool name.
/// </summary>
public sealed class ToolResult
{
    private ToolResult(bool isError, string text)
    {
        IsError = isError;
        Text = text;
    }

    public bool IsError { get; }

    public string Text { get; }

    public static ToolResult Ok(string text) => new(isError: false, text);

    /// <summary>Serializes <paramref name="payload"/> compactly into the single text content block.</summary>
    public static ToolResult Ok(JsonNode payload) => new(isError: false, payload.ToJsonString());

    public static ToolResult Fail(string message) => new(isError: true, message);

    /// <summary>Renders the MCP result shape: a one-element content array plus the isError flag.</summary>
    public JsonObject ToResultObject()
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = Text,
                },
            },
            ["isError"] = IsError,
        };
    }
}
