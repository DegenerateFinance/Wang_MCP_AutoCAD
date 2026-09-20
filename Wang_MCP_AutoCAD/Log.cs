namespace Wang_MCP_AutoCAD;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,

    /// <summary>Not a writable level — assign to <see cref="Log.MinimumLevel"/> to silence the log.</summary>
    Off,
}

/// <summary>
/// Thread-safe append-only file log. Opens and closes per write deliberately:
/// a cached FileStream in a static field would outlive MCPRELOAD's context
/// unload and keep a handle on the file across reloads.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wang_MCP_AutoCAD", "mcp.log");

    /// <summary>
    /// Messages below this level are dropped. Volatile because the HTTP listener
    /// threads read it while a command on the document thread may be changing it.
    /// </summary>
    private static volatile LogLevel _minimumLevel = LogLevel.Info;

    public static LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message, System.Exception? ex = null) => Write(LogLevel.Warn, message, ex);

    public static void Error(string message, System.Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Write(LogLevel level, string message, System.Exception? ex = null)
    {
        if (level < MinimumLevel || level == LogLevel.Off)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                   $"{level.ToString().ToUpperInvariant(),-5} " +
                   $"[{Environment.CurrentManagedThreadId,3}] {message}" +
                   (ex is null ? "" : $"{Environment.NewLine}{ex}");

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take down a command or an HTTP request.
            }
        }
    }
}
