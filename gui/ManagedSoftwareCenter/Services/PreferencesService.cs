// PreferencesService.cs - Reads MSC preferences from preferences.yaml

using System.IO;
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
        try
        {
            if (!File.Exists(PreferencesPath)) return;

            var content = await File.ReadAllTextAsync(PreferencesPath);
            var prefs = _deserializer.Deserialize<MscPreferences>(content);
            if (prefs == null) return;

            if (prefs.AggressiveNotificationDays > 0)
                AggressiveNotificationDays = prefs.AggressiveNotificationDays;

            HelpUrl = prefs.HelpUrl;
        }
        catch
        {
            // Use defaults if preferences can't be read
        }
    }

    private class MscPreferences
    {
        [YamlMember(Alias = "aggressive_notification_days")]
        public int AggressiveNotificationDays { get; set; } = 14;

        [YamlMember(Alias = "help_url")]
        public string? HelpUrl { get; set; }
    }
}
