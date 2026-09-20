using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Locks the active document and runs tool work inline.
///
/// This is called from within McpHttpServer's accept loop, which now runs as the body of
/// McpExtension.StartCommand — i.e. already on AutoCAD's document thread. There is therefore
/// nothing to marshal: LockDocument() either acquires immediately or blocks this call until
/// AutoCAD is free (a modal dialog or another command finishes), which is exactly the "don't
/// race against a busy UI thread" behaviour wanted, with no separate queue or timeout needed
/// to get it.
/// </summary>
internal sealed class AcadDocumentGateway : IDocumentGateway
{
    /// <summary>
    /// Refreshed on every call, so /health can name the current drawing.
    /// </summary>
    internal static volatile string CachedDrawingName = "(none)";

    public bool TryRun(Func<ToolResult> work, out ToolResult? outcome, out string failureReason)
    {
        outcome = null;
        failureReason = "";

        Stopwatch sw = Stopwatch.StartNew();

        Document doc = Application.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            failureReason = "No drawing is open in AutoCAD.";
            return false;
        }

        try
        {
            using (DocumentLock documentLock = doc.LockDocument())
            {
                CachedDrawingName = doc.Name;
                outcome = work();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            Log.Error($"op=gateway/run outcome=acad_error error_status={acEx.ErrorStatus}", acEx);
            failureReason = $"AutoCAD rejected the operation: {acEx.ErrorStatus}.";
            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error("op=gateway/run outcome=failed", ex);
            failureReason = "The operation failed inside AutoCAD; see the MCP log.";
            return false;
        }

        sw.Stop();
        Log.Debug($"op=gateway/run outcome=done elapsed_ms={sw.ElapsedMilliseconds}");
        return true;
    }
}
