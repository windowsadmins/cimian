// TriggerArgsPolicy.cs - Allow-list for the "Args:" line of a CimianWatcher flag file.

namespace Cimian.Core.Services;

/// <summary>
/// Decides which managedsoftwareupdate arguments a flag file may request.
/// </summary>
/// <remarks>
/// Flag files are written by the signed-in user (Managed Software Center, cimitrigger,
/// CimianStatus), and CimianWatcher runs managedsoftwareupdate as SYSTEM with whatever
/// the "Args:" line says. Passed through unchecked, that line let a standard user hand
/// SYSTEM a configuration or manifest file of their own (<c>--config</c>,
/// <c>--local-only-manifest</c>) and so run anything as SYSTEM.
///
/// Only the run modes the GUI tools actually request are accepted. Every option that
/// takes a path, names a different manifest, or changes engine state outside a normal
/// run is refused, as is anything this list does not know. The accepted line is rebuilt
/// from its parsed tokens rather than passed on verbatim, so what managedsoftwareupdate
/// parses is exactly what was checked.
/// </remarks>
public static class TriggerArgsPolicy
{
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        "--auto", "-a",
        "--checkonly", "-c",
        "--installonly", "-i",
        "--show-status",
        "--no-preflight",
        "--quiet", "-q",
        "-v", "-vv", "-vvv",
    };

    /// <summary>
    /// Validates a flag-file Args line. Returns true with the canonical argument string
    /// when every token is allowed; otherwise false with the reason.
    /// </summary>
    public static bool TrySanitize(string? argsLine, out string? sanitized, out string? reason)
    {
        sanitized = null;

        var tokens = BootstrapArgsBuilder.TokenizeWindowsCommandLine(argsLine ?? string.Empty);
        if (tokens.Count == 0)
        {
            reason = "no arguments";
            return false;
        }

        var output = new List<string>();
        var sawItem = false;
        var sawNoPreflight = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token == "--item")
            {
                if (sawItem)
                {
                    reason = "--item given more than once";
                    return false;
                }
                sawItem = true;
                output.Add(token);

                var names = 0;
                while (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                {
                    var name = tokens[++i];
                    if (name.Length == 0 || name.Any(char.IsControl))
                    {
                        reason = "invalid item name";
                        return false;
                    }
                    output.Add(BootstrapArgsBuilder.QuoteArgument(name));
                    names++;
                }
                if (names == 0)
                {
                    reason = "--item without an item name";
                    return false;
                }
                continue;
            }

            if (token == "--status-port")
            {
                if (i + 1 >= tokens.Count
                    || !int.TryParse(tokens[i + 1], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var port)
                    || port < 1 || port > 65535)
                {
                    reason = "--status-port needs a port number";
                    return false;
                }
                output.Add(token);
                output.Add(tokens[++i]);
                continue;
            }

            if (Switches.Contains(token))
            {
                sawNoPreflight |= token == "--no-preflight";
                output.Add(token);
                continue;
            }

            // Not echoed: logs are readable by every user.
            reason = $"argument {i + 1} is not allowed in a trigger file";
            return false;
        }

        // Managed Software Center skips preflight only for the targeted run behind a
        // Self Service click. A full run requested by a user always runs it.
        if (sawNoPreflight && !sawItem)
        {
            reason = "--no-preflight is only allowed with --item";
            return false;
        }

        sanitized = string.Join(' ', output);
        reason = null;
        return true;
    }

}
