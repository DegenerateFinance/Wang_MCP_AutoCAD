using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Carries work from a listener thread onto AutoCAD's document thread and back.
///
/// ExecuteInApplicationContext is one of the very few AutoCAD managed APIs legal to call
/// from an arbitrary thread: it queues the delegate onto the main message loop and returns.
/// The callback then runs in *application* context, which is why the explicit
/// doc.LockDocument() below is mandatory — AutoCAD does not auto-lock there, and a write
/// without the lock fails with eLockViolation.
///
/// AutoCAD only pumps these callbacks when it is quiescent, so a modal dialog or a command
/// awaiting input holds them off indefinitely. Every wait here is therefore bounded.
/// </summary>
internal sealed class AcadDocumentGateway : IDocumentGateway
{
    private readonly object _pendingGate = new();
    private readonly List<PendingCall> _pending = new();
    private volatile bool _shuttingDown;

    /// <summary>
    /// Refreshed on the document thread on every call, so the listener thread and /health
    /// can name the current drawing without touching MdiActiveDocument off-thread.
    /// </summary>
    internal static volatile string CachedDrawingName = "(none)";

    public bool TryRun(
        Func<ToolResult> work,
        int ms_Timeout,
        out ToolResult? outcome,
        out string failureReason)
    {
        outcome = null;
        failureReason = "";

        if (_shuttingDown)
        {
            failureReason = "The AutoCAD MCP server is shutting down.";
            return false;
        }

        PendingCall pending = new();
        lock (_pendingGate)
        {
            _pending.Add(pending);
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            Application.DocumentManager.ExecuteInApplicationContext(
                _ => RunOnDocumentThread(pending, work),
                null);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            Forget(pending);
            Log.Error($"op=gateway/enqueue outcome=failed error_status={acEx.ErrorStatus}", acEx);
            failureReason = "Could not queue work onto AutoCAD's main thread.";
            return false;
        }
        catch (System.Exception ex)
        {
            Forget(pending);
            Log.Error("op=gateway/enqueue outcome=failed", ex);
            failureReason = "Could not queue work onto AutoCAD's main thread.";
            return false;
        }

        bool completed = pending.Completion.Task.Wait(ms_Timeout);
        sw.Stop();

        if (!completed)
        {
            // The callback may still be sitting in AutoCAD's queue. Mark it abandoned so it
            // does not modify the drawing seconds after the client was told it failed.
            pending.Abandoned = true;
            Forget(pending);

            Log.Warn($"op=gateway/call outcome=timeout ms_timeout={ms_Timeout} elapsed_ms={sw.ElapsedMilliseconds}");
            failureReason =
                $"AutoCAD did not become available within {ms_Timeout} ms. It is most likely busy "
                + "with a command or a modal dialog — finish or cancel it in AutoCAD, then retry.";
            return false;
        }

        Forget(pending);

        if (pending.Completion.Task.IsCanceled)
        {
            failureReason = "The AutoCAD MCP server is shutting down.";
            return false;
        }

        outcome = pending.Completion.Task.Result;
        Log.Debug($"op=gateway/call outcome=done elapsed_ms={sw.ElapsedMilliseconds}");
        return true;
    }

    /// <summary>
    /// Releases every listener thread blocked waiting on the document thread.
    ///
    /// Shutdown runs ON the document thread, so those waiters can only ever be satisfied by
    /// the very thread trying to join them. Without this, each reload with a call in flight
    /// stalls for the full document-call timeout.
    /// </summary>
    public void AbortPending()
    {
        _shuttingDown = true;

        lock (_pendingGate)
        {
            foreach (PendingCall pending in _pending)
            {
                pending.Abandoned = true;
                pending.Completion.TrySetCanceled();
            }

            Log.Debug($"op=gateway/abort pending={_pending.Count}");
            _pending.Clear();
        }
    }

    private void Forget(PendingCall pending)
    {
        lock (_pendingGate)
        {
            _pending.Remove(pending);
        }
    }

    /// <summary>
    /// Runs on AutoCAD's main thread. Nothing may escape: an exception thrown out of an
    /// ExecuteInApplicationContext callback lands on the message loop and can take AutoCAD
    /// down.
    /// </summary>
    private static void RunOnDocumentThread(PendingCall pending, Func<ToolResult> work)
    {
        if (pending.Abandoned)
        {
            Log.Warn("op=gateway/callback outcome=abandoned reason=caller_timed_out_or_shutdown");
            return;
        }

        try
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc is null)
            {
                pending.Completion.TrySetResult(ToolResult.Fail("No drawing is open in AutoCAD."));
                return;
            }

            CachedDrawingName = doc.Name;

            using (DocumentLock documentLock = doc.LockDocument())
            {
                pending.Completion.TrySetResult(work());
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            Log.Error($"op=gateway/callback outcome=acad_error error_status={acEx.ErrorStatus}", acEx);
            pending.Completion.TrySetResult(ToolResult.Fail($"AutoCAD rejected the operation: {acEx.ErrorStatus}."));
        }
        catch (System.Exception ex)
        {
            Log.Error("op=gateway/callback outcome=failed", ex);
            pending.Completion.TrySetResult(ToolResult.Fail("The operation failed inside AutoCAD; see the MCP log."));
        }
    }

    /// <summary>
    /// A TaskCompletionSource rather than a ManualResetEventSlim on purpose: on timeout the
    /// listener thread walks away, and a disposed event would then be Set() by the late
    /// callback — throwing ObjectDisposedException on AutoCAD's message loop. A TCS has
    /// nothing to dispose and TrySetResult is always safe.
    /// </summary>
    private sealed class PendingCall
    {
        public readonly TaskCompletionSource<ToolResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Abandoned;
    }
}
