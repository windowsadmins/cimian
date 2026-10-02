// PreferencesService.cs - Reads MSC preferences from preferences.yaml
// and applies Policies\Cimian HelpURL overrides (same hive as agent CSP).

using System.IO;
using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Reads Managed Software Center preferences from C:\ProgramData\ManagedInstalls\preferences.yaml
/// and optional HKLM\SOFTWARE\Policies\Cimian policy overrides.
/// </summary>
public class PreferencesService : IPreferencesService
{
    private const string PreferencesPath = @"C:\ProgramData\ManagedInstalls\preferences.yaml";

    // Same hive as ConfigurationService.ApplyPolicyOverrides (CimianPrefs / Policy CSP).
    private const string PolicyRegistryPath = @"SOFTWARE\Policies\Cimian";
    private const string HelpUrlPolicyValue = "HelpURL";

    private readonly IDeserializer _deserializer;

    public int AggressiveNotificationDays { get; private set; } = 14;
    public string? HelpUrl { get; private set; }
    public List<string>? SidebarItems { get; private set; }

    public PreferencesService()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _ = ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        // Reset each reload so a removed YAML key / policy value does not stick.
        AggressiveNotificationDays = 14;
        HelpUrl = null;
        SidebarItems = null;

        try
        {
            if (File.Exists(PreferencesPath))
            {
                var content = await File.ReadAllTextAsync(PreferencesPath);
                var prefs = _deserializer.Deserialize<MscPreferences>(content);
                if (prefs != null)
                {
                    if (prefs.AggressiveNotificationDays > 0)
                        AggressiveNotificationDays = prefs.AggressiveNotificationDays;

                    HelpUrl = prefs.HelpUrl;

                    // Validate sidebar items — only allow known page tags
                    if (prefs.SidebarItems is { Count: > 0 })
                    {
                        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "software", "categories", "myitems", "updates" };
                        var valid = prefs.SidebarItems
                            .Where(s => allowed.Contains(s))
                            .Select(s => s.ToLowerInvariant())
                            .ToList();
                        SidebarItems = valid.Count > 0 ? valid : null;
                    }
                }
            }
        }
        catch
        {
            // Use defaults if preferences.yaml can't be read; policy may still apply.
        }

        // Policy wins over YAML when HelpURL is present under Policies\Cimian.
        ApplyHelpUrlPolicyOverride();
    }

    /// <summary>
    /// Reads HKLM\SOFTWARE\Policies\Cimian\HelpURL when the value name exists.
    /// Empty REG_SZ clears Help (hides the footer item). Omitting the value
    /// leaves preferences.yaml <c>help_url</c> in effect.
    /// </summary>
    private void ApplyHelpUrlPolicyOverride()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyRegistryPath, writable: false);
            if (key == null)
                return;

            // Presence of the value name matters — empty string is still an override.
            var names = key.GetValueNames();
            var hasHelpUrl = names.Any(n =>
                string.Equals(n, HelpUrlPolicyValue, StringComparison.OrdinalIgnoreCase));
            if (!hasHelpUrl)
                return;

            var raw = key.GetValue(HelpUrlPolicyValue);
            HelpUrl = raw switch
            {
                string s => s,
                null => string.Empty,
                _ => raw.ToString() ?? string.Empty
            };
        }
        catch
        {
            // Ignore registry access failures; keep YAML / defaults.
        }
    }

    private class MscPreferences
    {
        [YamlMember(Alias = "aggressive_notification_days")]
        public int AggressiveNotificationDays { get; set; } = 14;

        [YamlMember(Alias = "help_url")]
        public string? HelpUrl { get; set; }

        [YamlMember(Alias = "sidebar_items")]
        public List<string>? SidebarItems { get; set; }
    }
}
