// TriggerRequests.cs - Naming and parsing of the run requests CimianWatcher picks up.

namespace Cimian.Core.Services;

/// <summary>How a run request asks managedsoftwareupdate to run.</summary>
public enum TriggerRequestKind
{
    /// <summary>No Args line: the watcher's default arguments.</summary>
    Defaults,
    /// <summary>An Args line that passed <see cref="TriggerArgsPolicy"/>.</summary>
    Custom,
    /// <summary>Refused; nothing runs.</summary>
    Refused,
}

/// <summary>
/// Run requests are small text files. Administrators and SYSTEM may still use the fixed
/// <c>.cimian.bootstrap</c> / <c>.cimian.headless</c> names at the ManagedInstalls root.
/// User-context tools write one file per request to <see cref="CimianPaths.TriggersDir"/>,
/// so one user can never block or rewrite another's request.
/// </summary>
public static class TriggerRequests
{
    /// <summary>A request is a few lines; anything larger is refused unread.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    private const string GuiPrefix = ".cimian.bootstrap";
    private const string HeadlessPrefix = ".cimian.headless";

    /// <summary>A new, unique request file name in the triggers folder.</summary>
    public static string NewRequestPath(bool gui) =>
        Path.Combine(CimianPaths.TriggersDir, $"{(gui ? GuiPrefix : HeadlessPrefix)}.{Guid.NewGuid():N}");

    /// <summary>True for a GUI or headless request file name, with or without a unique suffix.</summary>
    public static bool IsRequestFile(string path)
    {
        var name = Path.GetFileName(path);
        return HasPrefix(name, GuiPrefix) || HasPrefix(name, HeadlessPrefix);
    }

    /// <summary>True when the request asks for a run with the status window.</summary>
    public static bool IsGuiRequest(string path) => HasPrefix(Path.GetFileName(path), GuiPrefix);

    private static bool HasPrefix(string name, string prefix) =>
        name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the Args lines of a request. Any refused line refuses the whole request. The
    /// reason never repeats the file's content, because logs\ is readable by every user.
    /// </summary>
    public static TriggerRequestKind Parse(string content, out string? args, out string? reason)
    {
        args = null;
        reason = null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Args:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TriggerArgsPolicy.TrySanitize(trimmed["Args:".Length..].Trim(), out var sanitized, out reason))
            {
                args = null;
                return TriggerRequestKind.Refused;
            }
            args = sanitized;
        }

        return args == null ? TriggerRequestKind.Defaults : TriggerRequestKind.Custom;
    }
}
