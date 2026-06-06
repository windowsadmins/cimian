// BootstrapArgsBuilder.cs - Pure helpers for composing CimianWatcher flag-file Args lines.
// Kept in Cimian.Core so the GUI (ManagedSoftwareCenter) and unit tests can share the
// quoting and merge logic without depending on WinUI.

using System.Text;

namespace Cimian.Core.Services;

/// <summary>
/// Pure, stateless helpers that compose the "Args:" line of the CimianWatcher
/// bootstrap flag file. Concentrating the logic here lets unit tests cover the
/// quoting and multi-item merge behavior without spinning up a TriggerService.
/// </summary>
public static class BootstrapArgsBuilder
{
    /// <summary>Args appended to every self-serve targeted install run.</summary>
    public const string SelfServeTrailingArgs = "--no-preflight --show-status -vv";

    /// <summary>
    /// Quotes a single argument for safe round-trip through a flag-file "Args:"
    /// line and ProcessStartInfo.Arguments. Follows the Windows C-runtime
    /// convention: backslash-escape any embedded double-quote and any run of
    /// backslashes immediately preceding a quote or the closing quote.
    /// Throws on control characters that cannot legally appear in an item name.
    /// </summary>
    public static string QuoteArgument(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw new ArgumentException("Argument contains control characters", nameof(value));
        }

        // Fast path: no whitespace, no quotes, no trailing backslash — no quoting needed.
        if (value.Length > 0
            && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
            && value[value.Length - 1] != '\\')
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0) sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Builds the full Args line for a self-serve targeted install:
    /// "--item N1 --item N2 ... --no-preflight --show-status -vv".
    /// Order of items is preserved from the input; duplicates (case-insensitive)
    /// are dropped so callers can pass raw click history without preprocessing.
    /// </summary>
    public static string BuildSelfServeInstallArgs(IEnumerable<string> itemNames)
    {
        if (itemNames == null) throw new ArgumentNullException(nameof(itemNames));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var name in itemNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(name)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("--item ");
            sb.Append(QuoteArgument(name));
        }

        if (sb.Length == 0)
        {
            throw new ArgumentException("At least one item name is required", nameof(itemNames));
        }

        sb.Append(' ');
        sb.Append(SelfServeTrailingArgs);
        return sb.ToString();
    }

    /// <summary>
    /// Parses an existing "Args:" line (as written by a previous flag-file
    /// write) and extracts the names that followed each "--item" token.
    /// Returns an empty list if no --item tokens are present or the line is null/blank.
    /// </summary>
    /// <remarks>
    /// This is intentionally tolerant: any tokens after --item that are themselves
    /// switches (start with --) are ignored. Quoted names produced by
    /// <see cref="QuoteArgument"/> are unquoted using the same Windows C-runtime
    /// rules.
    /// </remarks>
    public static IReadOnlyList<string> ExtractItemNames(string? argsLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(argsLine)) return result;

        var tokens = TokenizeWindowsCommandLine(argsLine);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i], "--item", StringComparison.Ordinal)) continue;
            if (i + 1 >= tokens.Count) break;
            var next = tokens[i + 1];
            if (next.StartsWith("--", StringComparison.Ordinal)) continue;
            result.Add(next);
        }
        return result;
    }

    /// <summary>
    /// Tokenizes a Windows command-line argument string using the C-runtime
    /// rules implemented by <see cref="QuoteArgument"/> (backslash-quote and
    /// run-of-backslashes-before-quote escaping). Pure inverse of QuoteArgument
    /// for the values it produces.
    /// </summary>
    internal static IReadOnlyList<string> TokenizeWindowsCommandLine(string input)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(input)) return tokens;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (c == '\\')
            {
                var backslashes = 0;
                while (i < input.Length && input[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < input.Length && input[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        i++;
                    }
                    hasToken = true;
                }
                else
                {
                    current.Append('\\', backslashes);
                    hasToken = true;
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                i++;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                i++;
            }
            else
            {
                current.Append(c);
                hasToken = true;
                i++;
            }
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
