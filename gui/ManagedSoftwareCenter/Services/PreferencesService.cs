// PreferencesService.cs - Reads MSC preferences from preferences.yaml
// and applies Policies\Cimian overrides (HelpURL / CategoryIcons CSP).

using System.IO;
using Cimian.Core;
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
    private const string CategoryIconsPolicyName = "CategoryIcons";

    private readonly IDeserializer _deserializer;

    public int AggressiveNotificationDays { get; private set; } = 14;
    public string? HelpUrl { get; private set; }
    public List<string>? SidebarItems { get; private set; }

    /// <summary>
    /// Category name → Segoe MDL2 glyph from preferences.yaml and/or Policies\Cimian.
    /// </summary>
    public IReadOnlyDictionary<string, string> CategoryIconGlyphs { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
        AggressiveNotificationDays = 14;
        HelpUrl = null;
        SidebarItems = null;
        CategoryIconGlyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                    // Resolve category_icons specs → glyphs (built-in key, hex, or raw glyph).
                    CategoryIconGlyphs = MscCategoryIconResolver.BuildOverrideGlyphs(prefs.CategoryIcons);
                }
            }
        }
        catch
        {
            // Use defaults if preferences.yaml can't be read; policy may still apply.
        }

        // Policy replaces YAML category_icons when CategoryIcons is present under Policies\Cimian.
        ApplyCategoryIconsPolicyOverride();

        // Push overrides into the shared glyph lookup used by Software/Categories pages.
        MscCategoryIconResolver.ConfigureOverrides(CategoryIconGlyphs);
    }

    /// <summary>
    /// Reads HKLM\SOFTWARE\Policies\Cimian\CategoryIcons when present.
    /// Supported shapes (first match wins):
    /// 1. REG_MULTI_SZ (or newline-separated REG_SZ) value named CategoryIcons —
    ///    each line <c>Category=iconSpec</c> or <c>Category: iconSpec</c>
    /// 2. Subkey CategoryIcons with REG_SZ values (name = category, data = icon spec)
    /// Presence of either form replaces preferences.yaml <c>category_icons</c>
    /// (empty MULTI_SZ / empty subkey clears overrides).
    /// </summary>
    private void ApplyCategoryIconsPolicyOverride()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyRegistryPath, writable: false);
            if (key == null)
                return;

            // 1) MULTI_SZ / string value on the Policies\Cimian key
            var valueNames = key.GetValueNames();
            var hasMultiValue = valueNames.Any(n =>
                string.Equals(n, CategoryIconsPolicyName, StringComparison.OrdinalIgnoreCase));
            if (hasMultiValue)
            {
                var raw = key.GetValue(CategoryIconsPolicyName);
                CategoryIconGlyphs = MscCategoryIconResolver.BuildOverrideGlyphsFromPolicyLines(
                    NormalizePolicyLines(raw));
                return;
            }

            // 2) Subkey with one REG_SZ per category (Registry Preferences friendly)
            using var subKey = key.OpenSubKey(CategoryIconsPolicyName, writable: false);
            if (subKey == null)
                return;

            var specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in subKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var raw = subKey.GetValue(name);
                var spec = raw switch
                {
                    string s => s,
                    null => string.Empty,
                    _ => raw.ToString() ?? string.Empty
                };
                if (!string.IsNullOrWhiteSpace(spec))
                    specs[name.Trim()] = spec.Trim();
            }

            CategoryIconGlyphs = MscCategoryIconResolver.BuildOverrideGlyphs(specs);
        }
        catch
        {
            // Ignore registry access failures; keep YAML / defaults.
        }
    }

    /// <summary>
    /// Normalizes registry MULTI_SZ / string payloads into individual policy lines.
    /// </summary>
    internal static IEnumerable<string> NormalizePolicyLines(object? raw)
    {
        switch (raw)
        {
            case string[] lines:
                return lines;
            case string s when s.Length > 0:
                return s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            default:
                return Array.Empty<string>();
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

        /// <summary>
        /// Map of category name → icon spec (built-in key, hex codepoint, or glyph).
        /// </summary>
        [YamlMember(Alias = "category_icons")]
        public Dictionary<string, string>? CategoryIcons { get; set; }
    }
}
