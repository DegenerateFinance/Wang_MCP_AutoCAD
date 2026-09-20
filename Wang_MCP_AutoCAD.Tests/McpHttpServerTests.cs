using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Drives the real <see cref="McpHttpServer"/> over a real loopback socket with a fake
/// document gateway. Nothing here touches AutoCAD — which is the point: the Origin guard,
/// the HTTP routing and the shutdown join are the parts most likely to break, and they are
/// all verifiable without opening a drawing.
/// </summary>
[Collection(LogGlobalCollection.Name)]
public class McpHttpServerTests : IDisposable
{
    private readonly McpServerOptions _options;
    private readonly McpHttpServer _server;
    private readonly HttpClient _client = new();
    private readonly int _port;

    public McpHttpServerTests()
    {
        _port = FreePort();
        _options = new McpServerOptions { Port = _port };

        ToolRegistry registry = new();
        registry.Add(PingTool.Create(_options, () => _server!.Snapshot()));

        McpDispatcher dispatcher = new(registry, new FakeDocumentGateway(), _options);
        _server = new McpHttpServer(dispatcher, _options, _port);

        Assert.True(_server.TryStart(out string error), error);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Join(2000);
        _client.Dispose();
    }

    /// <summary>
    /// HttpListener cannot bind port 0, so grab one via a throwaway TcpListener. The fixed
    /// 5599 must never be used here — a test run must not fight a live AutoCAD session.
    /// </summary>
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private string Url => $"http://127.0.0.1:{_port}/mcp";

    private HttpResponseMessage Post(string body, string? origin = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return _client.Send(request);
    }

    [Fact]
    public void Post_Initialize_Returns200AndTheNegotiatedProtocolVersion()
    {
        HttpResponseMessage response = Post(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode node = JsonNode.Parse(response.Content.ReadAsStringAsync().Result)!;
        Assert.Equal("2025-06-18", node["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Post_Notification_Returns202WithAnEmptyBody()
    {
        HttpResponseMessage response = Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", response.Content.ReadAsStringAsync().Result);
    }

    [Fact]
    public void Post_Garbage_Returns200WithAParseError()
    {
        HttpResponseMessage response = Post("}{");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode node = JsonNode.Parse(response.Content.ReadAsStringAsync().Result)!;
        Assert.Equal(JsonRpcErrorCode.ParseError, node["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Post_ToolsCallPing_ReturnsParsableJsonInsideTheContentText()
    {
        HttpResponseMessage response = Post(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"acad_ping","arguments":{}}}""");

        JsonNode payload = JsonNode.Parse(response.Content.ReadAsStringAsync().Result)!["result"]!;

        Assert.False(payload["isError"]!.GetValue<bool>());

        JsonNode inner = JsonNode.Parse(payload["content"]!.AsArray()[0]!["text"]!.GetValue<string>())!;
        Assert.Equal(Environment.ProcessId, inner["pid"]!.GetValue<int>());
    }

    [Fact]
    public void NonLoopbackOrigin_Is403()
    {
        HttpResponseMessage response = Post("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", origin: "http://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:8080")]
    public void LoopbackOrigin_IsAllowed(string origin)
    {
        HttpResponseMessage response = Post("""{"jsonrpc":"2.0","id":4,"method":"ping"}""", origin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void AbsentOrigin_IsAllowed_BecauseThatIsTheNormalMcpClient()
    {
        HttpResponseMessage response = Post("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Get_Mcp_Is405WithAnAllowHeader()
    {
        HttpResponseMessage response = _client.Send(new HttpRequestMessage(HttpMethod.Get, Url));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public void Delete_Mcp_Is204()
    {
        HttpResponseMessage response = _client.Send(new HttpRequestMessage(HttpMethod.Delete, Url));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public void UnknownPath_Is404()
    {
        HttpResponseMessage response = _client.Send(
            new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/nope")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void TrailingSlashOnTheMcpPath_RoutesToTheSameEndpoint()
    {
        // Registering the root and routing in code is what makes this work; a registered
        // "/mcp/" prefix is a known HTTP.SYS 404 trap for a request to "/mcp".
        HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{_port}/mcp/")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":6,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response = _client.Send(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Get_Health_ReportsThePortAndPid()
    {
        HttpResponseMessage response = _client.Send(
            new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode node = JsonNode.Parse(response.Content.ReadAsStringAsync().Result)!;
        Assert.Equal(_port, node["port"]!.GetValue<int>());
        Assert.Equal(Environment.ProcessId, node["pid"]!.GetValue<int>());
    }

    [Fact]
    public void Snapshot_CountsRequests()
    {
        Post("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        Post("""{"jsonrpc":"2.0","id":8,"method":"ping"}""");

        Assert.True(_server.Snapshot().Requests >= 2);
    }
}

/// <summary>
/// Separated from the fixture above because it must own the whole lifecycle: this is the
/// regression test for the constraint that makes MCPRELOAD work at all.
/// </summary>
[Collection(LogGlobalCollection.Name)]
public class McpHttpServerShutdownTests
{
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static McpHttpServer Build(int port)
    {
        McpServerOptions options = new() { Port = port };
        ToolRegistry registry = new();
        McpDispatcher dispatcher = new(registry, new FakeDocumentGateway(), options);
        return new McpHttpServer(dispatcher, options, port);
    }

    [Fact]
    public void Stop_JoinsWithinTheTimeoutAndFreesThePort()
    {
        // The load-bearing test. A listener thread that outlives Stop() pins the collectible
        // AssemblyLoadContext, and every MCPRELOAD then leaks a context plus the port.
        int port = FreePort();
        McpHttpServer server = Build(port);
        Assert.True(server.TryStart(out string error), error);

        using (HttpClient client = new())
        {
            client.Send(new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
            });
        }

        server.Stop();

        Assert.True(server.Join(5000), "The listener thread did not exit; the ALC would leak.");

        // The port must be genuinely released, not merely unreferenced.
        McpHttpServer rebound = Build(port);
        Assert.True(rebound.TryStart(out string rebindError), $"Port {port} was not released: {rebindError}");
        rebound.Stop();
        rebound.Join(5000);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        int port = FreePort();
        McpHttpServer server = Build(port);
        Assert.True(server.TryStart(out string _));

        server.Stop();
        server.Stop();

        Assert.True(server.Join(5000));
    }

    [Fact]
    public void Join_BeforeStart_ReturnsTrue()
    {
        McpHttpServer server = Build(FreePort());

        Assert.True(server.Join(10));
    }

    [Fact]
    public void TryStart_OnATakenPort_ReturnsFalseAndSaysWhy()
    {
        // The "fixed port, fail loud" decision: no scanning, no silent fallback.
        int port = FreePort();
        McpHttpServer first = Build(port);
        Assert.True(first.TryStart(out string _));

        try
        {
            McpHttpServer second = Build(port);

            Assert.False(second.TryStart(out string error));
            Assert.Contains(port.ToString(), error);
        }
        finally
        {
            first.Stop();
            first.Join(5000);
        }
    }
}
