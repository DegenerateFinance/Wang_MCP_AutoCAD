namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// <see cref="Log"/> is a process-global static: <c>MinimumLevel</c> and <c>Path</c> are
/// shared by every test in the run, and <see cref="LogTests"/> asserts on the log file's
/// length. xunit parallelizes test *classes*, so the moment a second class causes a log
/// write — and the MCP server logs one line per request — those assertions go flaky and
/// <c>MinimumLevel = Off</c> in one class silently suppresses logging another is relying on.
///
/// Every test class that can cause a <see cref="Log"/> write joins this collection.
/// Making <c>Log</c> injectable instead would be a larger refactor than a deliberately
/// global static warrants.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogGlobalCollection
{
    public const string Name = "log-global";
}
