using System;

namespace Cimian.Core.Services;

/// <summary>
/// Centralized console logging with verbosity control and clean output.
/// On a terminal: no timestamps, no log level prefixes - just clean colored messages,
/// where colors indicate the log level visually. When stdout (or stderr) is redirected
/// there is no terminal to read the colors and no clock to relate lines to, so each line
/// is prefixed with the same "[yyyy-MM-dd HH:mm:ss] LEVEL " stamp the log files use and
/// a captured transcript lines up with run.log.
/// 
/// - verbose 1+ (-v): info, majorStatus, minorStatus, warning, error
/// - verbose 2+ (-vv): detail messages  
/// - verbose 3+ (-vvv): debug1 messages
/// - verbose 4+ (-vvvv): debug2 messages
/// 
/// Errors and warnings are ALWAYS shown to console regardless of verbosity.
/// 
/// When a SessionLogger is attached via SetSessionLogger(), all output is also
/// written to the per-session run.log and reports/run.log for external monitoring.
/// </summary>
public static class ConsoleLogger
{
    // ANSI color codes
    private const string ColorReset = "\u001b[0m";
    private const string ColorGreen = "\u001b[32m";
    private const string ColorYellow = "\u001b[33m";
    private const string ColorRed = "\u001b[31m";
    private const string ColorCyan = "\u001b[36m";
    private const string ColorMagenta = "\u001b[35m";  // For debug2/trace level
    private const string ColorDim = "\u001b[2m";       // Dim/faint for extra detail

    /// <summary>
    /// Current verbosity level. Set this at application startup.
    /// Verbosity levels:
    /// 0 = quiet (errors/warnings only)
    /// 1 = normal (-v): info messages shown
    /// 2 = detail (-vv): detail messages shown
    /// 3 = debug1 (-vvv): debug1 messages shown
    /// 4 = debug2 (-vvvv): debug2 messages shown
    /// </summary>
    public static int Verbosity { get; set; } = 0;

    /// <summary>
    /// Whether to include indentation prefix for hierarchical output
    /// </summary>
    public static bool UseIndentation { get; set; } = false;

    /// <summary>
    /// Optional SessionLogger reference for writing to log files.
    /// When set, all console output is also written to the session run.log.
    /// </summary>
    private static SessionLogger? _sessionLogger;

    /// <summary>
    /// Attach a SessionLogger so all console output also routes to log files.
    /// Call this after creating the SessionLogger in UpdateEngine.
    /// </summary>
    public static void SetSessionLogger(SessionLogger? logger)
    {
        _sessionLogger = logger;
    }

    /// <summary>
    /// Overrides the redirection check for both stdout and stderr. Tests set it so the
    /// prefixed and unprefixed paths can each be exercised regardless of how the test
    /// host wires the console; null means ask the console.
    /// </summary>
    internal static bool? OutputRedirectedOverride { get; set; }

    private static bool OutputRedirected => OutputRedirectedOverride ?? Console.IsOutputRedirected;
    private static bool ErrorRedirected  => OutputRedirectedOverride ?? Console.IsErrorRedirected;

    private static void WriteOut(string level, string text)
        => Write(Console.Out, OutputRedirected, level, text);

    private static void WriteErr(string level, string text)
        => Write(Console.Error, ErrorRedirected, level, text);

    /// <summary>
    /// Writes <paramref name="text"/> as-is on a terminal, or one stamped line per line
    /// of it when the stream is redirected. The stamp matches <see cref="SessionLogger.Log"/>
    /// so a captured stdout and the file log read the same way.
    /// </summary>
    private static void Write(System.IO.TextWriter writer, bool redirected, string level, string text)
    {
        if (!redirected)
        {
            writer.WriteLine(text);
            return;
        }

        var stamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level,-5} ";
        foreach (var line in text.Split('\n'))
        {
            writer.WriteLine(stamp + line.TrimEnd('\r'));
        }
    }

    /// <summary>
    /// Write a message to the session logger if attached.
    /// Strips ANSI color codes and Unicode box-drawing characters before writing to log files.
    /// </summary>
    private static void LogToSession(string level, string message)
    {
        if (_sessionLogger == null) return;
        // Strip ANSI escape sequences for clean log file output
        var clean = System.Text.RegularExpressions.Regex.Replace(message, @"\x1b\[[0-9;]*m", "");
        // Replace Unicode box-drawing and symbol characters with ASCII equivalents for log compatibility
        clean = clean.Replace("├", "+").Replace("└", "+").Replace("─", "-").Replace("│", "|")
                      .Replace("→", "->").Replace("✓", "[OK]").Replace("✗", "[FAIL]");
        _sessionLogger.Log(level, clean);
    }

    /// <summary>
    /// Log a plain message (always shown) - no color
    /// </summary>
    public static void Log(string message = "")
    {
        WriteOut("INFO", message);
        LogToSession("INFO", message);
    }

    /// <summary>
    /// Log an info message (shown at verbose >= 1, i.e. -v or higher) - no color (default terminal)
    /// </summary>
    public static void Info(string message)
    {
        if (Verbosity >= 1)
        {
            WriteOut("INFO", message);
        }
        LogToSession("INFO", message);
    }

    /// <summary>
    /// Log a detail message (shown at verbose >= 2, i.e. -vv or higher) - cyan color (debug level)
    /// </summary>
    public static void Detail(string message)
    {
        if (Verbosity >= 2)
        {
            WriteOut("DEBUG", $"{ColorCyan}    {message}{ColorReset}");
        }
        LogToSession("DEBUG", message);
    }

    /// <summary>
    /// Log a debug1 message (shown at verbose >= 3, i.e. -vvv or higher) - cyan color
    /// </summary>
    public static void Debug(string message)
    {
        if (Verbosity >= 3)
        {
            WriteOut("DEBUG", $"{ColorCyan}    {message}{ColorReset}");
        }
        LogToSession("DEBUG", message);
    }

    /// <summary>
    /// Alias for Debug for compatibility
    /// </summary>
    public static void Debug1(string message) => Debug(message);

    /// <summary>
    /// Log a debug2/trace message (shown at verbose >= 4, i.e. -vvvv or higher) - cyan color
    /// </summary>
    public static void Debug2(string message)
    {
        if (Verbosity >= 4)
        {
            WriteOut("TRACE", $"{ColorCyan}    {message}{ColorReset}");
        }
        LogToSession("TRACE", message);
    }

    /// <summary>
    /// Alias for Debug2 for compatibility with existing code
    /// </summary>
    public static void Trace(string message) => Debug2(message);

    /// <summary>
    /// Log a success message (always shown) - green color
    /// </summary>
    public static void Success(string message)
    {
        WriteOut("INFO", $"{ColorGreen}{message}{ColorReset}");
        LogToSession("INFO", message);
    }

    /// <summary>
    /// Log a warning message (always shown) - yellow color
    /// </summary>
    public static void Warn(string message)
    {
        WriteOut("WARN", $"{ColorYellow}{message}{ColorReset}");
        LogToSession("WARN", message);
    }

    /// <summary>
    /// Log an error message (always shown) - red color, to stderr
    /// </summary>
    public static void Error(string message)
    {
        WriteErr("ERROR", $"{ColorRed}{message}{ColorReset}");
        LogToSession("ERROR", message);
    }

    /// <summary>
    /// Log with indentation - useful for hierarchical output
    /// </summary>
    public static void Indented(string message, int level = 1)
    {
        var indent = new string('\t', level);
        WriteOut("INFO", $"{indent}{message}");
    }

    /// <summary>
    /// Log a starred item (e.g. "* Processing manifest item...")
    /// </summary>
    public static void Item(string message)
    {
        WriteOut("INFO", $"* {message}");
    }

    /// <summary>
    /// Log a double-starred item (e.g. "** Processing conditional_items...")
    /// </summary>
    public static void SubItem(string message)
    {
        WriteOut("INFO", $"** {message}");
    }
}
