namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Tunables for the MCP server. Nothing here touches AutoCAD, so the whole protocol
/// stack can be constructed and driven from a unit test.
/// </summary>
public sealed class McpServerOptions
{
    /// <summary>Loopback only. Never "+" or "*" — those need elevation or a urlacl reservation.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// Fixed by decision: no scanning, no fallback. A second AutoCAD session simply
    /// fails to bind and says so, rather than silently landing on a port no client knows.
    /// </summary>
    public int Port { get; init; } = 5599;

    /// <summary>The JSON-RPC path. Routed in code, not registered as an HttpListener prefix.</summary>
    public string McpPath { get; init; } = "/mcp";

    /// <summary>
    /// How long a listener thread waits for AutoCAD's main thread to run a queued
    /// callback. AutoCAD only pumps these when it is quiescent, so a modal dialog or
    /// an in-progress command can hold one off indefinitely — hence a bounded wait.
    /// </summary>
    public int Ms_DocumentCallTimeout { get; init; } = 15000;

    /// <summary>
    /// How long shutdown waits for the listener thread to exit. A live thread pins the
    /// collectible AssemblyLoadContext, so overrunning this is logged as a leak.
    /// </summary>
    public int Sec_ListenerShutdown { get; init; } = 5;

    /// <summary>Request bodies larger than this are rejected with 413 rather than buffered.</summary>
    public int Bytes_MaxRequestBody { get; init; } = 1024 * 1024;

    public string ProtocolVersion { get; init; } = "2025-06-18";

    public string ServerName { get; init; } = "wang-mcp-autocad";

    public string ServerVersion { get; init; } = "1.0.0";

    public string BaseUrl => $"http://{Host}:{Port}";

    public string McpUrl => $"{BaseUrl}{McpPath}";
}
