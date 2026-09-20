namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Doubles as the wiring check for this project: <see cref="Log"/> is real plugin
/// code, and it runs here only because it carries no AutoCAD types. If a test ever
/// fails with a FileNotFoundException for acmgd/acdbmgd, the code under test has
/// crossed the boundary described in the .csproj — split it, don't reference the
/// AutoCAD DLLs from here.
/// </summary>
[Collection(LogGlobalCollection.Name)]
public class LogTests
{
    [Fact]
    public void MinimumLevel_RoundTrips()
    {
        LogLevel original = Log.MinimumLevel;
        try
        {
            Log.MinimumLevel = LogLevel.Debug;
            Assert.Equal(LogLevel.Debug, Log.MinimumLevel);

            Log.MinimumLevel = LogLevel.Off;
            Assert.Equal(LogLevel.Off, Log.MinimumLevel);
        }
        finally
        {
            Log.MinimumLevel = original;
        }
    }

    [Fact]
    public void Write_BelowMinimumLevel_DoesNotTouchTheLogFile()
    {
        LogLevel original = Log.MinimumLevel;
        try
        {
            Log.MinimumLevel = LogLevel.Off;

            bool existedBefore = File.Exists(Log.Path);
            long lengthBefore = existedBefore ? new FileInfo(Log.Path).Length : 0;

            Log.Debug("op=test_wiring msg=should_be_dropped");

            bool existsAfter = File.Exists(Log.Path);
            long lengthAfter = existsAfter ? new FileInfo(Log.Path).Length : 0;

            Assert.Equal(existedBefore, existsAfter);
            Assert.Equal(lengthBefore, lengthAfter);
        }
        finally
        {
            Log.MinimumLevel = original;
        }
    }
}
