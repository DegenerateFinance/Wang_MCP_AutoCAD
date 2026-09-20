using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Stands in for the AutoCAD document thread. Because <see cref="IDocumentGateway"/> is the
/// entire boundary, substituting this exercises the real dispatcher, the real tool handlers
/// and the real HTTP server with no AutoCAD in the process.
/// </summary>
internal sealed class FakeDocumentGateway : IDocumentGateway
{
    public bool ShouldFail { get; set; }

    public string FailureReason { get; set; } = "AutoCAD was not available.";

    public int RunCount { get; private set; }

    public bool TryRun(Func<ToolResult> work, out ToolResult? outcome, out string failureReason)
    {
        RunCount++;

        if (ShouldFail)
        {
            outcome = null;
            failureReason = FailureReason;
            return false;
        }

        failureReason = "";
        outcome = work();
        return true;
    }
}
