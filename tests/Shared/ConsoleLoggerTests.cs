using System;
using System.IO;
using Cimian.Core.Services;
using Xunit;

namespace Cimian.Tests.Shared;

/// <summary>
/// Console output is plain on a terminal and stamped when captured.
/// </summary>
/// <remarks>
/// The regression this guards: console output carried no timestamp or level, so a
/// stdout captured by a scheduler or a wrapper script could not be lined up against
/// run.log, and a warning and an info line were told apart by colour codes alone.
/// </remarks>
public class ConsoleLoggerTests : IDisposable
{
    private const string Stamp = @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ";

    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly int _originalVerbosity = ConsoleLogger.Verbosity;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public ConsoleLoggerTests()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        ConsoleLogger.SetSessionLogger(null);
        ConsoleLogger.Verbosity = 2;
    }

    public void Dispose()
    {
        ConsoleLogger.OutputRedirectedOverride = null;
        ConsoleLogger.Verbosity = _originalVerbosity;
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }

    private static string[] Lines(StringWriter writer)
        => writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void RedirectedOutputCarriesTheSameStampAsTheFileLog()
    {
        ConsoleLogger.OutputRedirectedOverride = true;

        ConsoleLogger.Log("plain");
        ConsoleLogger.Warn("careful");
        ConsoleLogger.Detail("fine print");
        ConsoleLogger.Error("broken");

        var outLines = Lines(_stdout);
        Assert.Equal(3, outLines.Length);
        Assert.Matches(Stamp + "INFO  plain$", outLines[0]);
        Assert.Matches(Stamp + "WARN  careful$", outLines[1]);
        Assert.Matches(Stamp + "DEBUG     fine print$", outLines[2]);

        var errLines = Lines(_stderr);
        Assert.Single(errLines);
        Assert.Matches(Stamp + "ERROR broken$", errLines[0]);

        // No control bytes reach the transcript: colours are meaningless once captured.
        Assert.DoesNotContain('\u001b', _stdout.ToString());
        Assert.DoesNotContain('\u001b', _stderr.ToString());
    }

    [Fact]
    public void RedirectedOutputReplacesBoxDrawingWithAscii()
    {
        ConsoleLogger.OutputRedirectedOverride = true;

        ConsoleLogger.Log("└─ ✓ done → next");

        Assert.Matches(Stamp + @"INFO  \+- \[OK\] done -> next$", Lines(_stdout)[0]);
    }

    [Fact]
    public void RedirectedMultiLineMessagesStampEveryLine()
    {
        ConsoleLogger.OutputRedirectedOverride = true;

        ConsoleLogger.Log("first\nsecond");

        var lines = Lines(_stdout);
        Assert.Equal(2, lines.Length);
        Assert.Matches(Stamp + "INFO  first$", lines[0]);
        Assert.Matches(Stamp + "INFO  second$", lines[1]);
    }

    [Fact]
    public void TerminalOutputIsLeftExactlyAsItWas()
    {
        ConsoleLogger.OutputRedirectedOverride = false;

        ConsoleLogger.Log("plain");
        ConsoleLogger.Warn("careful");
        ConsoleLogger.Error("broken");

        Assert.Equal(new[] { "plain", "[33mcareful[0m" }, Lines(_stdout));
        Assert.Equal(new[] { "[31mbroken[0m" }, Lines(_stderr));
    }
}
