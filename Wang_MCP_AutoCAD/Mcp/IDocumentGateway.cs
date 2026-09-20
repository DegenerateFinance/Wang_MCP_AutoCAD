namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The second half of the protocol/AutoCAD boundary. Implemented on the Acad side; the
/// protocol side never sees a Document, a Transaction or a DocumentLock — only "run this
/// on the right thread". A test substitutes a fake that runs the work inline, which is what
/// lets the whole tools/call path be exercised with no AutoCAD present.
/// </summary>
public interface IDocumentGateway
{
    /// <summary>
    /// Runs <paramref name="work"/> on AutoCAD's document thread with the active document
    /// locked. Returns false when the work never ran at all — no active document, timed out,
    /// or the server is shutting down — and <paramref name="failureReason"/> is then a
    /// client-facing sentence. Must never throw: an escape here would surface on the
    /// listener thread and abort the request loop.
    /// </summary>
    bool TryRun(
        Func<ToolResult> work,
        int ms_Timeout,
        out ToolResult? outcome,
        out string failureReason);

    /// <summary>
    /// Releases every caller currently blocked waiting on the document thread.
    ///
    /// Shutdown runs ON the document thread, so a listener thread waiting for a queued
    /// callback can only ever be satisfied by the thread that is trying to join it. Calling
    /// this before the join is not an optimisation — without it every reload with a call in
    /// flight stalls for the full document-call timeout.
    /// </summary>
    void AbortPending();
}
