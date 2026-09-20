using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>Counters read out of the listener for MCPSTATUS and the shutdown summary.</summary>
public readonly record struct ServerStats(
    long Requests,
    long Failures,
    long Ms_RequestTotal,
    long Ms_Uptime)
{
    public long Ms_RequestAverage => Requests == 0 ? 0 : Ms_RequestTotal / Requests;
}

/// <summary>
/// The HTTP surface. HttpListener and System.Text.Json are both in-box BCL, so this type
/// deliberately sits on the testable side of the boundary: the Origin guard, the routing and
/// — most importantly — the deterministic shutdown are all exercised over a real loopback
/// socket with no AutoCAD in the process.
/// </summary>
public sealed class McpHttpServer
{
    private readonly McpDispatcher _dispatcher;
    private readonly McpServerOptions _options;
    private readonly int _port;

    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _stopping;
    private readonly Stopwatch _sw_Uptime = new();

    private long _requestCount;
    private long _failureCount;
    private long _ms_RequestTotal;

    public McpHttpServer(McpDispatcher dispatcher, McpServerOptions options)
        : this(dispatcher, options, options.Port)
    {
    }

    /// <summary>Port override exists for tests, which must not fight over the fixed 5599.</summary>
    public McpHttpServer(McpDispatcher dispatcher, McpServerOptions options, int port)
    {
        _dispatcher = dispatcher;
        _options = options;
        _port = port;
    }

    public int Port => _port;

    public string McpUrl => $"http://{_options.Host}:{_port}{_options.McpPath}";

    public bool IsRunning => _listener is not null && !_stopping;

    /// <summary>
    /// Binds the fixed port and starts the accept thread. Returns false rather than throwing:
    /// per the port decision there is no scan and no fallback, so a taken port is reported
    /// to the user and the server stays down.
    /// </summary>
    public bool TryStart(out string error)
    {
        error = "";

        HttpListener listener = new();

        // Bind the root rather than a "/mcp/" prefix. HTTP.SYS prefix matching for a
        // registered "…/mcp/" against a request for "…/mcp" is a genuine 404 trap, and
        // routing on the path in code costs nothing on a dedicated loopback port.
        listener.Prefixes.Add($"http://{_options.Host}:{_port}/");

        // A client that hangs up mid-response must not throw out of the write.
        listener.IgnoreWriteExceptions = true;

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            error = $"Could not bind {_options.Host}:{_port} (Windows error {ex.ErrorCode}). "
                  + "Another AutoCAD session or process is probably already using it.";
            Log.Error($"op=listener/start port={_port} outcome=bind_failed win32={ex.ErrorCode}", ex);
            listener.Close();
            return false;
        }
        catch (System.Exception ex)
        {
            error = $"Could not start the listener on {_options.Host}:{_port}.";
            Log.Error($"op=listener/start port={_port} outcome=failed", ex);
            listener.Close();
            return false;
        }

        _listener = listener;
        _stopping = false;
        _sw_Uptime.Restart();

        _thread = new Thread(Listen)
        {
            IsBackground = true,
            Name = "mcp-listener",
        };
        _thread.Start();

        Log.Info($"op=listener/start port={_port} url={McpUrl} outcome=listening");
        return true;
    }

    /// <summary>
    /// Unblocks the accept thread. Close() rather than Stop() is what throws out of a thread
    /// parked in GetContext() and releases the port.
    /// </summary>
    public void Stop()
    {
        _stopping = true;

        HttpListener? listener = _listener;
        if (listener is null)
        {
            return;
        }

        try
        {
            listener.Close();
        }
        catch (System.Exception ex)
        {
            Log.Warn("op=listener/close outcome=threw", ex);
        }
    }

    /// <summary>
    /// Joins the accept thread. Returns false on timeout — which means a thread is still
    /// executing code in the collectible AssemblyLoadContext and that context will leak.
    /// </summary>
    public bool Join(int ms_Timeout)
    {
        Thread? thread = _thread;
        if (thread is null)
        {
            return true;
        }

        bool joined = thread.Join(ms_Timeout);
        if (joined)
        {
            _thread = null;
            _listener = null;
        }

        return joined;
    }

    public ServerStats Snapshot()
    {
        return new ServerStats(
            Requests: Interlocked.Read(ref _requestCount),
            Failures: Interlocked.Read(ref _failureCount),
            Ms_RequestTotal: Interlocked.Read(ref _ms_RequestTotal),
            Ms_Uptime: _sw_Uptime.ElapsedMilliseconds);
    }

    private void Listen()
    {
        try
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener!.GetContext();
                }
                catch (HttpListenerException ex)
                {
                    if (_stopping)
                    {
                        Log.Debug($"op=listener/accept outcome=stopping win32={ex.ErrorCode}");
                        break;
                    }

                    Log.Error($"op=listener/accept outcome=failed win32={ex.ErrorCode}", ex);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    Log.Warn("op=listener/accept outcome=not_listening", ex);
                    break;
                }

                Handle(context);
            }
        }
        catch (System.Exception ex)
        {
            Log.Error("op=listener/accept outcome=unhandled", ex);
        }
        finally
        {
            _sw_Uptime.Stop();
            ServerStats stats = Snapshot();
            Log.Info(
                $"op=listener/shutdown port={_port} requests={stats.Requests} failures={stats.Failures} "
                + $"ms_request_total={stats.Ms_RequestTotal} ms_request_avg={stats.Ms_RequestAverage} "
                + $"ms_uptime={stats.Ms_Uptime}");
        }
    }

    private void Handle(HttpListenerContext context)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string method = context.Request.HttpMethod;
        string path = (context.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        try
        {
            // DNS-rebinding guard: the MCP spec's one-if defence. An absent Origin is the
            // normal non-browser client and is allowed.
            string? origin = context.Request.Headers["Origin"];
            if (!IsLoopbackOrigin(origin))
            {
                Interlocked.Increment(ref _failureCount);
                Log.Warn($"op=http method={method} path={path} origin={origin} outcome=forbidden_origin");
                Respond(context, 403, "text/plain; charset=utf-8", "Forbidden: non-loopback Origin.");
                return;
            }

            if (!string.Equals(path, _options.McpPath, StringComparison.Ordinal))
            {
                if (string.Equals(path, "/health", StringComparison.Ordinal) && method == "GET")
                {
                    RespondHealth(context);
                    Log.Info($"op=http method=GET path=/health status=200 elapsed_ms={sw.ElapsedMilliseconds}");
                    return;
                }

                Log.Warn($"op=http method={method} path={path} outcome=not_found");
                Respond(context, 404, "text/plain; charset=utf-8", "Not found. The MCP endpoint is " + _options.McpPath + ".");
                return;
            }

            if (method == "DELETE")
            {
                // Session teardown. This server is stateless, so there is nothing to forget.
                Respond(context, 204, null, null);
                Log.Info($"op=http method=DELETE path={path} status=204 elapsed_ms={sw.ElapsedMilliseconds}");
                return;
            }

            if (method != "POST")
            {
                // A server that offers no server-initiated SSE stream answers GET with 405;
                // the spec sanctions exactly this.
                context.Response.AddHeader("Allow", "POST, DELETE");
                Respond(context, 405, "text/plain; charset=utf-8", "Method not allowed. POST a JSON-RPC request.");
                Log.Warn($"op=http method={method} path={path} status=405 elapsed_ms={sw.ElapsedMilliseconds}");
                return;
            }

            if (context.Request.ContentLength64 > _options.Bytes_MaxRequestBody)
            {
                Interlocked.Increment(ref _failureCount);
                Respond(context, 413, "text/plain; charset=utf-8", "Request body too large.");
                Log.Warn($"op=http method=POST path={path} status=413 bytes={context.Request.ContentLength64}");
                return;
            }

            string body = ReadBody(context.Request);

            Interlocked.Increment(ref _requestCount);

            DispatchResult result = _dispatcher.Dispatch(body);

            if (result.ResponseJson is null)
            {
                Respond(context, result.HttpStatus, null, null);
            }
            else
            {
                Respond(context, result.HttpStatus, "application/json; charset=utf-8", result.ResponseJson);
            }

            sw.Stop();
            Interlocked.Add(ref _ms_RequestTotal, sw.ElapsedMilliseconds);

            if (result.IsFailure)
            {
                Interlocked.Increment(ref _failureCount);
            }

            // One line per request: Info on success, Error on failure, as CLAUDE.md requires.
            // A JSON-RPC error counts as a failure even though the HTTP exchange succeeded.
            string toolPart = string.IsNullOrEmpty(result.ToolName) ? "" : $" tool={result.ToolName}";
            Log.Write(
                result.IsFailure ? LogLevel.Error : LogLevel.Info,
                $"op=http method=POST path={path} rpc_method={result.MethodName}{toolPart} "
                + $"status={result.HttpStatus} outcome={result.Outcome} elapsed_ms={sw.ElapsedMilliseconds}");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Interlocked.Increment(ref _failureCount);
            Log.Error($"op=http method={method} path={path} outcome=unhandled elapsed_ms={sw.ElapsedMilliseconds}", ex);

            try
            {
                Respond(context, 500, "text/plain; charset=utf-8", "Internal server error.");
            }
            catch (System.Exception nested)
            {
                Log.Warn("op=http outcome=error_response_failed", nested);
            }
        }
    }

    private void RespondHealth(HttpListenerContext context)
    {
        ServerStats stats = Snapshot();
        JsonObject health = new()
        {
            ["server"] = _options.ServerName,
            ["version"] = _options.ServerVersion,
            ["protocolVersion"] = _options.ProtocolVersion,
            ["pid"] = Environment.ProcessId,
            ["port"] = _port,
            ["url"] = McpUrl,
            ["requests"] = stats.Requests,
            ["failures"] = stats.Failures,
            ["ms_uptime"] = stats.Ms_Uptime,
        };

        Respond(context, 200, "application/json; charset=utf-8", health.ToJsonString());
    }

    private string ReadBody(HttpListenerRequest request)
    {
        Encoding encoding = request.ContentEncoding ?? Encoding.UTF8;
        using StreamReader reader = new(request.InputStream, encoding);
        return reader.ReadToEnd();
    }

    private static void Respond(HttpListenerContext context, int status, string? contentType, string? body)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = status;

        if (contentType is not null)
        {
            response.ContentType = contentType;
        }

        if (body is null)
        {
            response.ContentLength64 = 0;
            response.Close();
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    /// <summary>
    /// An absent Origin means a non-browser client, which is not a rebinding vector. A
    /// present one must resolve to loopback — Uri.IsLoopback covers 127.0.0.0/8, ::1 and
    /// the literal "localhost".
    /// </summary>
    internal static bool IsLoopbackOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.IsLoopback;
    }
}
