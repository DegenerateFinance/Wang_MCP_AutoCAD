using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Wang_MCP_AutoCAD.Acad;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD;

/// <summary>
/// The plugin entry point. AutoCAD calls Initialize() on NETLOAD; in the dev loop
/// Loader.cs reflects for IExtensionApplication implementations and calls it instead.
///
/// Keep exactly one IExtensionApplication in this assembly: Loader.LoadPlugin instantiates
/// *every* one it finds, so a second would start a second listener on every reload.
///
/// The listener does not auto-start. Run MCPSTART to bind the port and serve — and note
/// that in the dev loop the commands below are not auto-registered by AutoCAD (that scan
/// only happens for NETLOAD'ed assemblies), so they are reached through the Loader's
/// MCPINVOKE.
/// </summary>
public sealed class McpExtension : IExtensionApplication
{
    /// <summary>
    /// State is static because MCPINVOKE builds a *fresh* instance of a command's declaring
    /// type via Activator.CreateInstance. An instance field set by MCPSTART would therefore
    /// be invisible to Terminate() on the real instance, and a listener still running would
    /// survive the reload — pinning the collectible AssemblyLoadContext forever.
    ///
    /// These statics live in that same collectible context and die with it, so they do not
    /// themselves defeat unloading.
    /// </summary>
    private static readonly object Gate = new();

    private static McpHttpServer? _server;
    private static readonly McpServerOptions Options = new();

    public void Initialize()
    {
        Log.Info($"op=extension/initialize port={Options.Port} autostart=false outcome=ready");

        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        ed?.WriteMessage(
            $"\nWang_MCP_AutoCAD ready. Run MCPSTART to listen on {Options.McpUrl}.\n"
            + $"Log: {Log.Path}\n");
    }

    public void Terminate()
    {
        Stopwatch sw = Stopwatch.StartNew();
        bool stopped = RequestStop(out string message);
        sw.Stop();

        Log.Info($"op=extension/terminate stopped={stopped} detail=\"{message}\" elapsed_ms={sw.ElapsedMilliseconds}");
    }

    /// <summary>
    /// Binds the port and then serves for as long as AutoCAD keeps this command "in
    /// progress" — the accept loop is awaited here directly, not handed off to a background
    /// thread, so this call does not return until MCPSTOP (or Terminate on unload) stops it.
    /// If a request handler lets an exception escape unexpectedly, it propagates out of this
    /// await too: AutoCAD reports the command as failed rather than the listener dying
    /// silently on a thread nobody is watching.
    ///
    /// async void is deliberate, not an oversight: AutoCAD's command executor invokes
    /// [CommandMethod]s without awaiting a returned Task, so returning Task here would just
    /// mean the executor treats the command as already finished — async void is the only
    /// shape that keeps the command "in progress" for the loop's whole lifetime.
    /// </summary>
    [CommandMethod("MCPSTART")]
    public async void StartCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;

        McpHttpServer server;
        lock (Gate)
        {
            if (_server is not null)
            {
                ed?.WriteMessage($"\nWang_MCP_AutoCAD: already listening on {_server.McpUrl}.\n");
                return;
            }

            AcadDocumentGateway gateway = new();

            ToolRegistry registry = new();
            registry.Add(PingTool.Create(Options, SnapshotStats));
            DrawingTools.RegisterAll(registry);

            McpDispatcher dispatcher = new(registry, gateway, Options);
            server = new McpHttpServer(dispatcher, Options);

            if (!server.TryStart(out string error))
            {
                ed?.WriteMessage($"\nWang_MCP_AutoCAD: could not start. {error}\n");
                return;
            }

            _server = server;
        }

        ed?.WriteMessage($"\nWang_MCP_AutoCAD listening on {server.McpUrl}.\nRun MCPSTOP to stop it.\n");

        try
        {
            // This is the listener's entire lifetime: MCPSTART stays "in progress" until
            // MCPSTOP (a separate command invocation) or Terminate() calls server.Stop(),
            // which closes the HttpListener and unblocks the awaited GetContextAsync() inside.
            await server.RunAcceptLoopAsync(CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            Log.Error("op=extension/listener outcome=unhandled", ex);
            throw;
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_server, server))
                {
                    _server = null;
                }
            }
        }
    }

    [CommandMethod("MCPSTOP")]
    public void StopCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        RequestStop(out string message);
        ed?.WriteMessage($"\nWang_MCP_AutoCAD: {message}\n");
    }

    [CommandMethod("MCPSTATUS")]
    public void StatusCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null)
        {
            return;
        }

        McpHttpServer? server;
        lock (Gate)
        {
            server = _server;
        }

        if (server is null)
        {
            ed.WriteMessage($"\nWang_MCP_AutoCAD: not listening. Run MCPSTART to bind {Options.McpUrl}.\n");
            return;
        }

        ServerStats stats = server.Snapshot();
        ed.WriteMessage(
            $"\nWang_MCP_AutoCAD listening on {server.McpUrl}"
            + $"\n  pid           {Environment.ProcessId}"
            + $"\n  requests      {stats.Requests}"
            + $"\n  failures      {stats.Failures}"
            + $"\n  avg ms/req    {stats.Ms_RequestAverage}"
            + $"\n  uptime ms     {stats.Ms_Uptime}"
            + $"\n  log           {Log.Path} ({Log.MinimumLevel})\n");
    }

    private static ServerStats SnapshotStats()
    {
        McpHttpServer? server = _server;
        return server is null ? new ServerStats(0, 0, 0, 0) : server.Snapshot();
    }

    /// <summary>
    /// Signals the running listener to stop and returns immediately — it does not wait for
    /// StartCommand's awaited RunAcceptLoopAsync to actually return. There is nothing to join
    /// here any more: StartCommand's own async method is the loop's lifetime, so once
    /// server.Stop() closes the listener, that await unwinds on its own and clears _server
    /// itself (see the finally block in StartCommand).
    /// </summary>
    private static bool RequestStop(out string message)
    {
        lock (Gate)
        {
            McpHttpServer? server = _server;
            if (server is null)
            {
                message = "not listening.";
                return true;
            }

            server.Stop();
            message = "stopping.";
            return true;
        }
    }
}
