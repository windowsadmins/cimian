// InstallTrigger.cs - The check that decided a package needed to run

using System.Text.Json.Serialization;

namespace Cimian.Core.Models;

/// <summary>
/// The concrete detection result that made Cimian decide a package needed to be
/// (re)installed: which check ran, what it looked at, and what it found.
/// <para>
/// LoopGuard's warnings used to report only the counting rule that tripped
/// ("Rapid-fire loop: 3 installs within 2 hours"), which says a loop exists but
/// nothing about its cause — every diagnosis then started with an SSH session and
/// a hand-read of the pkgsinfo. The trigger is captured at the moment the decision
/// is made and replayed in the suppression message, so the warning itself names the
/// installs entry, installcheck_script or product code that never converges.
/// </para>
/// </summary>
public sealed class InstallTrigger
{
    /// <summary>Machine-readable code from <see cref="StatusReasonCode"/> (e.g. file_missing).</summary>
    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Which check produced it, from <see cref="DetectionMethod"/> (e.g. installs_array).</summary>
    [JsonPropertyName("detection_method")]
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>Human-readable detail, including the path/GUID/script output that decided it.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    /// <summary>Version found on disk at decision time, when the check determined one.</summary>
    [JsonPropertyName("installed_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledVersion { get; set; }

    /// <summary>Longest detail retained. Script output can run to hundreds of lines.</summary>
    public const int MaxDetailLength = 300;

    /// <summary>
    /// Builds a trigger from a status check, or null when there is nothing worth
    /// recording. Detail is flattened to a single line and truncated: it is carried
    /// in a warning message and in state that is rewritten every session.
    /// </summary>
    public static InstallTrigger? From(string? reasonCode, string? detectionMethod, string? detail, string? installedVersion = null)
    {
        var flat = Flatten(detail);
        if (string.IsNullOrEmpty(reasonCode) && string.IsNullOrEmpty(flat))
            return null;

        return new InstallTrigger
        {
            ReasonCode = reasonCode ?? string.Empty,
            DetectionMethod = detectionMethod ?? Models.DetectionMethod.None,
            Detail = flat,
            InstalledVersion = string.IsNullOrWhiteSpace(installedVersion) ? null : installedVersion
        };
    }

    /// <summary>Stable identity for counting how often the same trigger recurs.</summary>
    [JsonIgnore]
    public string Key => $"{ReasonCode}|{DetectionMethod}|{Detail}";

    /// <summary>
    /// One-line operator-facing form: the code, the check that produced it, and the
    /// detail. Reads as "why does this keep wanting to install".
    /// </summary>
    public string Describe()
    {
        var head = string.IsNullOrEmpty(ReasonCode) ? "check" : ReasonCode;
        var method = string.IsNullOrEmpty(DetectionMethod) || DetectionMethod == Models.DetectionMethod.None
            ? null
            : DetectionMethod;
        var lead = method == null ? head : $"{head} via {method}";
        return string.IsNullOrEmpty(Detail) ? lead : $"{lead} — {Detail}";
    }

    private static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaxDetailLength
            ? collapsed[..MaxDetailLength] + "…"
            : collapsed;
    }
}
