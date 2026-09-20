using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The one tool with no AutoCAD anywhere in its path — RequiresDocument is false, so it
/// never touches the document thread and answers even with no drawing open. That makes it
/// both the first thing to curl and the tool that lets a unit test drive a real tools/call
/// end to end with no AutoCAD in the process.
/// </summary>
public static class PingTool
{
    public static McpTool Create(McpServerOptions options, Func<ServerStats> stats)
    {
        return new McpTool
        {
            Name = "acad_ping",
            Description =
                "Checks that the AutoCAD MCP server is alive and reports which AutoCAD process it "
                + "is driving. Takes no arguments. Use this first to confirm connectivity before "
                + "calling any drawing tool.",
            InputSchema = ToolSchema.Object().Build(),
            RequiresDocument = false,
            Handler = _ =>
            {
                ServerStats snapshot = stats();

                JsonObject payload = new()
                {
                    ["server"] = options.ServerName,
                    ["version"] = options.ServerVersion,
                    ["protocolVersion"] = options.ProtocolVersion,
                    ["pid"] = Environment.ProcessId,
                    ["port"] = options.Port,
                    ["url"] = options.McpUrl,
                    ["ms_uptime"] = snapshot.Ms_Uptime,
                    ["requests"] = snapshot.Requests,
                    ["failures"] = snapshot.Failures,
                    ["ms_request_avg"] = snapshot.Ms_RequestAverage,
                    ["logPath"] = Log.Path,
                    ["logLevel"] = Log.MinimumLevel.ToString(),
                };

                return ToolResult.Ok(payload);
            },
        };
    }
}
