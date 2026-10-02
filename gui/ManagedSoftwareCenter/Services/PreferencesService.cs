// PreferencesService.cs - Reads MSC preferences from preferences.yaml

using System.IO;
using Cimian.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cimian.GUI.ManagedSoftwareCenter.Services;

/// <summary>
/// Reads Managed Software Center preferences from C:\ProgramData\ManagedInstalls\preferences.yaml
/// </summary>
public class PreferencesService : IPreferencesService
{
    private const string PreferencesPath = @"C:\ProgramData\ManagedInstalls\preferences.yaml";
    private readonly IDeserializer _deserializer;

    public int AggressiveNotificationDays { get; private set; } = 14;
    public string? HelpUrl { get; private set; }
    public List<string>? SidebarItems { get; private set; }

    /// <summary>
    /// Category name → Segoe MDL2 glyph, resolved from preferences.yaml category_icons.
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
            // Use defaults if preferences can't be read
        }

        // Push overrides into the shared glyph lookup used by Software/Categories pages.
        MscCategoryIconResolver.ConfigureOverrides(CategoryIconGlyphs);
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
