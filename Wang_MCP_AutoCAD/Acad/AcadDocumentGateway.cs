using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Resolves the target document, locks it and runs tool work inline.
///
/// This is called from within McpHttpServer's accept loop, which now runs as the body of
/// McpExtension.StartCommand — i.e. already on AutoCAD's document thread. There is therefore
/// nothing to marshal: LockDocument() either acquires immediately or blocks this call until
/// AutoCAD is free (a modal dialog or another command finishes), which is exactly the "don't
/// race against a busy UI thread" behaviour wanted, with no separate queue or timeout needed
/// to get it.
///
/// A READ may target a non-active document: it is locked directly, never activated, and this
/// was verified live. A WRITE may not — verified live, a write from inside MCPSTART to a
/// non-active document fails with eWrongDatabase, because MCPSTART is a document-context
/// command bound to whichever drawing was active. The fix that was NOT taken is making
/// MCPSTART a CommandFlags.Session command: that would mean re-verifying the whole inline
/// accept-loop design, for a capability the workflow does not need. Writes are refused here
/// instead, up front and with a message that names the fix. See
/// tests/results/PHASE3B_DRAWINGS_RESULTS.md.
/// </summary>
internal sealed class AcadDocumentGateway : IDocumentGateway
{
    /// <summary>
    /// Refreshed on every call, so /health can name the current drawing.
    /// </summary>
    internal static volatile string CachedDrawingName = "(none)";

    /// <summary>
    /// The document the in-flight tool call targets, set only while its work runs.
    /// AcadContext.TryOpen reads it, which is how a tool learns its drawing without every
    /// handler signature growing a Document parameter. A plain static is safe: every call
    /// runs on the one document thread, one at a time.
    /// </summary>
    internal static Document? CurrentDocument { get; private set; }

    public bool TryRun(
        string? drawing,
        bool requireActiveDrawing,
        Func<ToolResult> work,
        out ToolResult? outcome,
        out string failureReason)
    {
        outcome = null;
        failureReason = "";

        Stopwatch sw = Stopwatch.StartNew();

        if (!TryResolveDocument(drawing, requireActiveDrawing, out Document? doc, out bool refusedNotActive, out failureReason))
        {
            string reason = refusedNotActive ? "refused_not_active" : "no_match";
            Log.Warn($"op=gateway/run requested_drawing=\"{drawing}\" require_active={requireActiveDrawing} outcome={reason}");
            return false;
        }

        Document target = doc!;
        bool isActive = ReferenceEquals(target, Application.DocumentManager.MdiActiveDocument);

        try
        {
            using (DocumentLock documentLock = target.LockDocument())
            {
                // Make the target the WORKING database for the duration of the call. Some API
                // calls consult HostApplicationServices.WorkingDatabase rather than the
                // entity's own database — DBText.AdjustAlignment is the one hit live (Phase 4:
                // eInvalidContext even after the entity was appended, with the database passed
                // explicitly). The working database follows the drawing MCPSTART was started
                // in, not the drawing the user is on now, so with several drawings open it is
                // routinely the wrong one. Restored afterwards so the call leaves no trace.
                Database previousWorking = HostApplicationServices.WorkingDatabase;
                bool switchedWorking = !ReferenceEquals(previousWorking, target.Database);

                Log.Debug($"op=gateway/run drawing=\"{target.Name}\" is_active={isActive} working_db_was_target={!switchedWorking}");

                try
                {
                    if (switchedWorking)
                    {
                        HostApplicationServices.WorkingDatabase = target.Database;
                    }

                    CachedDrawingName = target.Name;
                    CurrentDocument = target;
                    outcome = work();
                }
                finally
                {
                    if (switchedWorking)
                    {
                        HostApplicationServices.WorkingDatabase = previousWorking;
                    }
                }
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            Log.Error($"op=gateway/run drawing=\"{target.Name}\" is_active={isActive} outcome=acad_error error_status={acEx.ErrorStatus}", acEx);
            failureReason = $"AutoCAD rejected the operation: {acEx.ErrorStatus}.";
            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error($"op=gateway/run drawing=\"{target.Name}\" is_active={isActive} outcome=failed", ex);
            failureReason = "The operation failed inside AutoCAD; see the MCP log.";
            return false;
        }
        finally
        {
            CurrentDocument = null;
        }

        sw.Stop();
        Log.Debug($"op=gateway/run drawing=\"{target.Name}\" is_active={isActive} outcome=done elapsed_ms={sw.ElapsedMilliseconds}");
        return true;
    }

    private static bool TryResolveDocument(
        string? drawing,
        bool requireActiveDrawing,
        out Document? doc,
        out bool refusedNotActive,
        out string failureReason)
    {
        doc = null;
        refusedNotActive = false;
        failureReason = "";

        DocumentCollection documents = Application.DocumentManager;

        if (drawing is null)
        {
            doc = documents.MdiActiveDocument;
            if (doc is null)
            {
                failureReason = "No drawing is open in AutoCAD.";
                return false;
            }

            return true;
        }

        List<Document> open = OpenDocuments.List();
        List<DrawingInfo> infos = OpenDocuments.Describe(open);

        if (!DrawingSelector.TrySelect(infos, drawing, requireActive: false, out int index, out failureReason))
        {
            return false;
        }

        // Matched first, then the active check, so the log can tell a typo from a refusal.
        if (requireActiveDrawing && !infos[index].IsActive)
        {
            refusedNotActive = true;
            failureReason = DrawingSelector.DescribeNotActive(infos, infos[index].Name);
            return false;
        }

        doc = open[index];
        return true;
    }
}

/// <summary>
/// Enumerating the open documents, shared by the gateway and acad_list_drawings so both see
/// the same list in the same order.
/// </summary>
internal static class OpenDocuments
{
    public static List<Document> List()
    {
        List<Document> open = new();
        foreach (Document doc in Application.DocumentManager)
        {
            open.Add(doc);
        }

        return open;
    }

    public static List<DrawingInfo> Describe(IReadOnlyList<Document> open)
    {
        Document? active = Application.DocumentManager.MdiActiveDocument;
        List<DrawingInfo> infos = new(open.Count);

        foreach (Document doc in open)
        {
            infos.Add(new DrawingInfo(doc.Name, ReferenceEquals(doc, active)));
        }

        return infos;
    }
}
