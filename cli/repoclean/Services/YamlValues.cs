using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using YamlDotNet.RepresentationModel;

namespace Cimian.CLI.Repoclean.Services;

/// <summary>
/// Reads untyped pkgsinfo and manifest values the same way whichever parser produced them.
/// YAML comes back as <see cref="Dictionary{TKey,TValue}"/> and <see cref="List{T}"/>
/// (or <c>Dictionary&lt;object, object&gt;</c> from YamlDotNet's deserializer), JSON as
/// <see cref="JObject"/> and <see cref="JArray"/>. Checking for only one of those shapes
/// is how requires, uninstaller and every manifest list used to be read as empty.
/// </summary>
public static class YamlValues
{
    public static Dictionary<string, object> ParseYamlMapping(string data)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(data));

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("the document is not a YAML mapping");
        }

        return ConvertMapping(root);
    }

    public static List<string> AsStringList(object? value)
    {
        return value switch
        {
            null => new List<string>(),
            string s => string.IsNullOrEmpty(s) ? new List<string>() : new List<string> { s },
            JValue j => j.Value is null ? new List<string>() : new List<string> { j.ToString() },
            IEnumerable<object> items => items
                .Select(i => i?.ToString() ?? string.Empty)
                .Where(i => !string.IsNullOrEmpty(i))
                .ToList(),
            _ => new List<string>()
        };
    }

    public static IReadOnlyDictionary<string, object>? AsMapping(object? value)
    {
        return value switch
        {
            Dictionary<string, object> d => d,
            IDictionary<object, object> d => d.ToDictionary(kvp => kvp.Key?.ToString() ?? string.Empty, kvp => kvp.Value),
            JObject j => j.Properties().ToDictionary(p => p.Name, p => (object)p.Value),
            _ => null
        };
    }

    /// <summary>A single mapping or a list of mappings, as a list.</summary>
    public static List<IReadOnlyDictionary<string, object>> AsMappingList(object? value)
    {
        if (AsMapping(value) is { } single)
        {
            return new List<IReadOnlyDictionary<string, object>> { single };
        }

        if (value is IEnumerable<object> items)
        {
            return items.Select(AsMapping).Where(m => m != null).Select(m => m!).ToList();
        }

        return new List<IReadOnlyDictionary<string, object>>();
    }

    public static string? AsString(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            JValue j => j.Value?.ToString(),
            _ => null
        };
    }

    /// <summary>
    /// A repo-relative path in one form for comparison: forward slashes, no leading slash.
    /// A pkgsinfo writes <c>apps/Tool.msi</c>; listing <c>pkgs\</c> on Windows yields
    /// <c>apps\Tool.msi</c>. Compared as they come, the two never match and every payload
    /// in the repository reads as orphaned.
    /// </summary>
    public static string NormalizeRepoPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        return path.Replace('\\', '/').TrimStart('/');
    }

    private static Dictionary<string, object> ConvertMapping(YamlMappingNode mapping)
    {
        var result = new Dictionary<string, object>();
        foreach (var kvp in mapping.Children)
        {
            if (kvp.Key is YamlScalarNode key)
            {
                result[key.Value ?? string.Empty] = ConvertNode(kvp.Value);
            }
        }
        return result;
    }

    private static object ConvertNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => scalar.Value ?? string.Empty,
            YamlSequenceNode sequence => sequence.Children.Select(ConvertNode).ToList(),
            YamlMappingNode mapping => ConvertMapping(mapping),
            _ => string.Empty
        };
    }
}
