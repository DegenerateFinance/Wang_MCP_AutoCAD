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
/// The listener does not auto-start. Run MCPSTART to bind the port — and note that in the
/// dev loop the commands below are not auto-registered by AutoCAD (that scan only happens
/// for NETLOAD'ed assemblies), so they are reached through the Loader's MCPINVOKE.
/// </summary>
public sealed class McpExtension : IExtensionApplication
{
    /// <summary>
    /// State is static because MCPINVOKE builds a *fresh* instance of a command's declaring
    /// type via Activator.CreateInstance. An instance field set by MCPSTART would therefore
    /// be invisible to Terminate() on the real instance, and the listener thread would
    /// survive the reload — pinning the collectible AssemblyLoadContext forever.
    ///
    /// These statics live in that same collectible context and die with it, so they do not
    /// themselves defeat unloading.
    /// </summary>
    private static readonly object Gate = new();

    private static McpHttpServer? _server;
    private static AcadDocumentGateway? _gateway;
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
        bool stopped = StopServer(out string message);
        sw.Stop();

        Log.Info($"op=extension/terminate stopped={stopped} detail=\"{message}\" elapsed_ms={sw.ElapsedMilliseconds}");
    }

    [CommandMethod("MCPSTART")]
    public void StartCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;

        lock (Gate)
        {
            if (_server is not null && _server.IsRunning)
            {
                ed?.WriteMessage($"\nWang_MCP_AutoCAD: already listening on {_server.McpUrl}.\n");
                return;
            }

            AcadDocumentGateway gateway = new();

            ToolRegistry registry = new();
            registry.Add(PingTool.Create(Options, SnapshotStats));
            DrawingTools.RegisterAll(registry);

            McpDispatcher dispatcher = new(registry, gateway, Options);
            McpHttpServer server = new(dispatcher, Options);

            if (!server.TryStart(out string error))
            {
                ed?.WriteMessage($"\nWang_MCP_AutoCAD: could not start. {error}\n");
                return;
            }

            _gateway = gateway;
            _server = server;

            ed?.WriteMessage(
                $"\nWang_MCP_AutoCAD listening on {server.McpUrl} ({registry.Count} tools).\n"
                + "Run MCPSTOP to stop it.\n");
        }
    }

    [CommandMethod("MCPSTOP")]
    public void StopCommand()
    {
        Editor? ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        StopServer(out string message);
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

        lock (Gate)
        {
            if (_server is null || !_server.IsRunning)
            {
                ed.WriteMessage($"\nWang_MCP_AutoCAD: not listening. Run MCPSTART to bind {Options.McpUrl}.\n");
                return;
            }

            ServerStats stats = _server.Snapshot();
            ed.WriteMessage(
                $"\nWang_MCP_AutoCAD listening on {_server.McpUrl}"
                + $"\n  pid           {Environment.ProcessId}"
                + $"\n  requests      {stats.Requests}"
                + $"\n  failures      {stats.Failures}"
                + $"\n  avg ms/req    {stats.Ms_RequestAverage}"
                + $"\n  uptime ms     {stats.Ms_Uptime}"
                + $"\n  log           {Log.Path} ({Log.MinimumLevel})\n");
        }
    }

    private static ServerStats SnapshotStats()
    {
        McpHttpServer? server = _server;
        return server is null ? new ServerStats(0, 0, 0, 0) : server.Snapshot();
    }

    /// <summary>
    /// Deterministic shutdown. The ordering is load-bearing, not stylistic: a listener
    /// thread still executing pins the collectible AssemblyLoadContext, and this method runs
    /// on the document thread — the very thread a pending tool call is waiting for.
    /// </summary>
    private static bool StopServer(out string message)
    {
        lock (Gate)
        {
            McpHttpServer? server = _server;
            AcadDocumentGateway? gateway = _gateway;

            if (server is null)
            {
                message = "not listening.";
                return true;
            }

            // 0. Release anyone blocked on the document thread FIRST. We are ON that thread,
            //    so a waiter can never be satisfied while we are here; joining before
            //    cancelling would stall for the full document-call timeout every reload.
            gateway?.AbortPending();

            // 1./2. Close() is what throws out of a thread parked in GetContext().
            server.Stop();

            // 3. Bounded join. Overrunning it means the context leaks — say so out loud.
            int ms_Join = Options.Sec_ListenerShutdown * 1000;
            bool joined = server.Join(ms_Join);

            _server = null;
            _gateway = null;

            if (!joined)
            {
                Log.Error($"op=listener/join outcome=timeout ms_join={ms_Join} note=alc_will_leak");
                message = $"listener did not stop within {Options.Sec_ListenerShutdown}s; the reload will leak a load context.";
                return false;
            }

            message = "stopped.";
            return true;
        }
    }
}
