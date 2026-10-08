using System.Reflection;
using Microsoft.Win32;
using YamlDotNet.Serialization;

namespace Cimian.Core.Services;

/// <summary>
/// The machine-wide sources a Cimian setting can come from, and how a value from one
/// of them is applied to a settings object.
/// </summary>
/// <remarks>
/// Precedence, highest first: a command-line flag for this run, then policy
/// (HKLM\SOFTWARE\Policies\Cimian), then machine settings (HKLM\SOFTWARE\Cimian\Settings),
/// then Config.yaml, then the built-in default. Callers load Config.yaml over the defaults,
/// apply the settings layer and then the policy layer with <see cref="Apply{T}"/>, and set
/// command-line flags last. Environment variables are never a source.
///
/// Every public read/write property of the settings object can be set by both registry
/// layers, under the same name it has in Config.yaml. Values are read by type:
/// a string from REG_SZ or REG_EXPAND_SZ; a boolean from REG_DWORD (0 or non-zero) or a
/// string (true/false, 1/0, yes/no); a number from REG_DWORD, REG_QWORD or a string;
/// a list from REG_MULTI_SZ, or from REG_SZ with one entry per line. A blank string or
/// an empty list counts as not set, so a lower layer still applies.
/// </remarks>
public static class SettingsLayers
{
    public const string PolicyRegistryPath = @"SOFTWARE\Policies\Cimian";
    public const string MachineSettingsRegistryPath = @"SOFTWARE\Cimian\Settings";

    public const string PolicySource = "policy";
    public const string MachineSettingsSource = "machine settings";

    /// <summary>
    /// Reads every value under HKLM\<paramref name="subKeyPath"/> in the 64-bit view, or
    /// null when the key is absent or cannot be read. The 64-bit view is where policy is
    /// written whatever the bitness of the reader.
    /// </summary>
    public static IReadOnlyDictionary<string, object>? ReadMachineKey(string subKeyPath)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                return null;
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is { } value)
                {
                    values[name] = value;
                }
            }
            return values;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Could not read HKLM\\{subKeyPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The setting names <typeparamref name="T"/> exposes, with the type each is read as.</summary>
    public static IReadOnlyList<(string Name, Type Type)> SettingNames<T>() =>
        Settable(typeof(T)).Select(s => (s.Name, s.Property.PropertyType)).ToList();

    /// <summary>
    /// Copies every value in <paramref name="values"/> that names a setting of
    /// <paramref name="target"/> onto it, and returns the names it set.
    /// </summary>
    /// <param name="validate">
    /// Optional check run on each parsed value; a non-null result is the reason the value
    /// is rejected, and the lower layer's value stands.
    /// </param>
    /// <param name="warn">Receives one line per value that was present but unusable.</param>
    public static List<string> Apply<T>(
        T target,
        IReadOnlyDictionary<string, object>? values,
        string sourceName,
        Func<string, object, string?>? validate = null,
        Action<string>? warn = null) where T : class
    {
        var applied = new List<string>();
        if (values is null || values.Count == 0)
        {
            return applied;
        }

        foreach (var setting in Settable(typeof(T)))
        {
            if (!TryGet(values, setting.Name, out var raw))
            {
                continue;
            }

            var type = setting.Property.PropertyType;
            if (!TryConvert(raw, type, out var parsed))
            {
                // Blank strings and empty lists are "not set", not an error.
                if (!IsBlank(raw))
                {
                    warn?.Invoke($"{setting.Name} in {sourceName} is not a valid {Describe(type)}; ignored");
                }
                continue;
            }

            if (validate?.Invoke(setting.Name, parsed!) is { } reason)
            {
                warn?.Invoke($"{setting.Name} in {sourceName} ignored: {reason}");
                continue;
            }

            setting.Property.SetValue(target, parsed);
            applied.Add(setting.Name);
        }

        return applied;
    }

    /// <summary>
    /// Converts a registry value to <paramref name="type"/>. False for a blank string, an
    /// empty list, or a value that does not parse.
    /// </summary>
    public static bool TryConvert(object? raw, Type type, out object? value)
    {
        value = null;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            if (raw is string s && !string.IsNullOrWhiteSpace(s))
            {
                value = s.Trim();
                return true;
            }
            return false;
        }

        if (underlying == typeof(bool))
        {
            if (TryBool(raw, out var b))
            {
                value = b;
                return true;
            }
            return false;
        }

        if (underlying == typeof(int))
        {
            if (TryInt(raw, out var i))
            {
                value = i;
                return true;
            }
            return false;
        }

        if (underlying == typeof(List<string>))
        {
            var entries = raw switch
            {
                string[] multi => multi,
                string single => single.Split('\n'),
                _ => null
            };
            var list = entries?
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .ToList();
            if (list is { Count: > 0 })
            {
                value = list;
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool TryBool(object? raw, out bool value)
    {
        switch (raw)
        {
            case int i:
                value = i != 0;
                return true;
            case long l:
                value = l != 0;
                return true;
            case string s:
                switch (s.Trim().ToLowerInvariant())
                {
                    case "1" or "true" or "yes":
                        value = true;
                        return true;
                    case "0" or "false" or "no":
                        value = false;
                        return true;
                }
                break;
        }
        value = false;
        return false;
    }

    private static bool TryInt(object? raw, out int value)
    {
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;
            case string s when int.TryParse(s.Trim(), out var parsed):
                value = parsed;
                return true;
        }
        value = 0;
        return false;
    }

    private static bool IsBlank(object raw) => raw switch
    {
        string s => string.IsNullOrWhiteSpace(s),
        string[] multi => multi.All(string.IsNullOrWhiteSpace),
        _ => false
    };

    private static string Describe(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(bool)) return "boolean";
        if (underlying == typeof(int)) return "number";
        if (underlying == typeof(List<string>)) return "list";
        return "string";
    }

    private static bool TryGet(IReadOnlyDictionary<string, object> values, string name, out object raw)
    {
        if (values.TryGetValue(name, out raw!))
        {
            return true;
        }
        // A dictionary built elsewhere may be case-sensitive; registry names are not.
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                raw = pair.Value;
                return true;
            }
        }
        return false;
    }

    private sealed record SettableProperty(string Name, PropertyInfo Property);

    /// <summary>
    /// Public instance properties with a public setter and a type this class can read,
    /// named as they are in Config.yaml (the YamlMember alias when there is one).
    /// </summary>
    private static IEnumerable<SettableProperty> Settable(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Where(p => IsSupported(p.PropertyType))
            .Where(p => p.GetCustomAttribute<YamlIgnoreAttribute>() is null)
            .Select(p => new SettableProperty(p.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? p.Name, p));

    private static bool IsSupported(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(string) || underlying == typeof(bool) ||
               underlying == typeof(int) || underlying == typeof(List<string>);
    }
}
