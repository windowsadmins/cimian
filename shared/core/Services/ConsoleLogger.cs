using System;

namespace Cimian.Core.Services;

/// <summary>
/// Centralized console logging with verbosity control and Munki-style clean output.
/// No timestamps, no log level prefixes - just clean colored messages.
/// Colors indicate the log level visually.
/// 
/// - verbose 1+ (-v): info, majorStatus, minorStatus, warning, error
/// - verbose 2+ (-vv): detail messages  
/// - verbose 3+ (-vvv): debug1 messages
/// - verbose 4+ (-vvvv): debug2 messages
/// 
/// Errors and warnings are ALWAYS shown to console regardless of verbosity.
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
    /// Matches Munki Swift verbosity (DisplayOptions.verbose):
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
    /// Log a plain message (always shown) - no color
    /// </summary>
    public static void Log(string message = "")
    {
        Console.WriteLine(message);
    }

    /// <summary>
    /// Log an info message (shown at verbose >= 1, i.e. -v or higher) - no color (default terminal)
    /// Matches Munki: if verbose > 0 { print }
    /// </summary>
    public static void Info(string message)
    {
        if (Verbosity >= 1)
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Log a detail message (shown at verbose >= 2, i.e. -vv or higher) - cyan color (debug level)
    /// Matches Munki: if verbose > 1 { print("    \(message)") }
    /// </summary>
    public static void Detail(string message)
    {
        if (Verbosity >= 2)
        {
            Console.WriteLine($"{ColorCyan}    {message}{ColorReset}");
        }
    }

    /// <summary>
    /// Log a debug1 message (shown at verbose >= 3, i.e. -vvv or higher) - cyan color
    /// Matches Munki: if verbose > 2 { print("    \(message)") }
    /// </summary>
    public static void Debug(string message)
    {
        if (Verbosity >= 3)
        {
            Console.WriteLine($"{ColorCyan}    {message}{ColorReset}");
        }
    }

    /// <summary>
    /// Alias for Debug for compatibility
    /// </summary>
    public static void Debug1(string message) => Debug(message);

    /// <summary>
    /// Log a debug2/trace message (shown at verbose >= 4, i.e. -vvvv or higher) - cyan color
    /// Matches Munki: if verbose > 3 { print("    \(message)") }
    /// </summary>
    public static void Debug2(string message)
    {
        if (Verbosity >= 4)
        {
            Console.WriteLine($"{ColorCyan}    {message}{ColorReset}");
        }
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
        Console.WriteLine($"{ColorGreen}{message}{ColorReset}");
    }

    /// <summary>
    /// Log a warning message (always shown) - yellow color
    /// </summary>
    public static void Warn(string message)
    {
        Console.WriteLine($"{ColorYellow}{message}{ColorReset}");
    }

    /// <summary>
    /// Log an error message (always shown) - red color, to stderr
    /// </summary>
    public static void Error(string message)
    {
        Console.Error.WriteLine($"{ColorRed}{message}{ColorReset}");
    }

    /// <summary>
    /// Log with indentation - useful for hierarchical output like Munki's style
    /// </summary>
    public static void Indented(string message, int level = 1)
    {
        var indent = new string('\t', level);
        Console.WriteLine($"{indent}{message}");
    }

    /// <summary>
    /// Log a starred item (like Munki's "* Processing manifest item...")
    /// </summary>
    public static void Item(string message)
    {
        Console.WriteLine($"* {message}");
    }

    /// <summary>
    /// Log a double-starred item (like Munki's "** Processing conditional_items...")
    /// </summary>
    public static void SubItem(string message)
    {
        Console.WriteLine($"** {message}");
    }
}
