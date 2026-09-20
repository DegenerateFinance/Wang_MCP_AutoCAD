namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The second half of the protocol/AutoCAD boundary. Implemented on the Acad side; the
/// protocol side never sees a Document, a Transaction or a DocumentLock — only "run this
/// with the document locked". A test substitutes a fake that runs the work inline, which is
/// what lets the whole tools/call path be exercised with no AutoCAD present.
///
/// There is no timeout and no cancellation here by design: the accept loop that calls this
/// now runs on AutoCAD's own document thread (see McpExtension.StartCommand), so a request
/// arriving while AutoCAD is busy with a modal dialog or another command simply waits for
/// Document.LockDocument() to become available, same as any other AutoCAD API call made on
/// that thread — not a bug to guard against, the natural behaviour of being on that thread.
/// </summary>
public interface IDocumentGateway
{
    /// <summary>
    /// Runs <paramref name="work"/> with the active document locked. Returns false when there
    /// is no active document; <paramref name="failureReason"/> is then a client-facing
    /// sentence. Must never throw: an escape here would surface on the accept loop and abort
    /// the request loop, taking MCPSTART's command down with it.
    /// </summary>
    bool TryRun(Func<ToolResult> work, out ToolResult? outcome, out string failureReason);
}
