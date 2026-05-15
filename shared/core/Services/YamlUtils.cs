using System.Collections;
using System.Reflection;
using Cimian.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Cimian.Core.Services;

/// <summary>
/// Single source of truth for Cimian YAML serialization (pkginfo, manifest,
/// catalog, InstallInfo). Every Cimian CLI tool and CimianStudio routes through
/// here, matching the MunkiStudio/yamlutils.swift pattern. The canonical form
/// produced here matches what real deployment files in deployment/pkgsinfo and
/// deployment/manifests look like, so saves don't churn diffs.
/// </summary>
public static class YamlUtils
{
    // Reader: Cimian deliberately tolerates unknown keys so older clients keep
    // working when new fields are added upstream.
    //
    // Why no WithNamingConvention(Underscored):
    // YamlDotNet 16.3's NamingConventionTypeInspector wraps the
    // YamlAttributesTypeInspector and re-applies the convention to the
    // already-aliased name. That silently rewrites `[YamlMember(Alias="OnDemand")]`
    // into `on_demand` on read — which is why `OnDemand: true` pkginfo files
    // (e.g. ProvisioningManifestEnrollment.yaml) deserialize as `false` under
    // the old SerializerBuilder. Every Cimian YAML model already decorates
    // each property with an explicit Alias, so dropping the convention is
    // safe and fixes the bug.
    public static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    // Writer: OmitNull + OmitEmptyCollections matches the union of every
    // pre-consolidation SerializerBuilder. We deliberately do NOT enable
    // OmitDefaults globally — pkginfo files in the wild always include
    // `unattended_install: false` explicitly (181 files surveyed; zero omit it),
    // and OmitDefaults would silently drop them.
    // LiteralMultilineEmitter forces `|` style for any scalar with `\n` —
    // YamlDotNet's default `>` (folded) would mangle PowerShell scripts.
    // No naming convention: see Deserializer comment above for the rationale.
    public static ISerializer Serializer { get; } = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .WithIndentedSequences()
        .WithEventEmitter(next => new LiteralMultilineEmitter(next))
        .Build();

    // Pkginfo key priority. Anything not listed sorts alphabetically after
    // these three (matches the SortedDictionary in the pre-consolidation
    // SerializePkgsInfoWithKeyOrder). `_metadata` is always last.
    private static readonly string[] PkgInfoPriorityKeys = { "name", "display_name", "version" };
    private const string MetadataKey = "_metadata";

    /// <summary>
    /// Serializes a pkginfo to the canonical Cimian form:
    /// name → display_name → version → (alphabetical) → _metadata last.
    /// Matches the pre-consolidation cimiimport SerializePkgsInfoWithKeyOrder
    /// output. The custom key ordering exists so files written by cimiimport,
    /// makepkginfo, autopkg, and hand-edits all converge on the same on-disk
    /// shape — otherwise CimianStudio saves would churn diffs.
    /// </summary>
    public static string SerializePkgInfo<T>(T pkgInfo) where T : class
    {
        // Normalize multi-line strings in script/description fields so YamlDotNet
        // emits clean `|` blocks (CRLF inside a literal scalar emits as CRLF
        // bytes, which causes Windows-vs-Linux churn).
        NormalizeMultilineStrings(pkgInfo);

        var raw = Serializer.Serialize(pkgInfo);
        return ReorderTopLevelKeys(raw, PkgInfoPriorityKeys, MetadataKey);
    }

    public static T? DeserializePkgInfo<T>(string yaml) where T : class
        => Deserializer.Deserialize<T>(yaml);

    /// <summary>
    /// Serializes a manifest, normalizing `included_manifests` entries from
    /// Windows `\` to forward `/` (manifests are URL paths consumed by Cimian
    /// agents on every platform). Done here so every caller — manifestutil,
    /// CimianStudio, etc. — gets it without copy-pasting the loop.
    /// </summary>
    public static string SerializeManifest<T>(T manifest) where T : class
    {
        NormalizeIncludedManifestPaths(manifest);
        return Serializer.Serialize(manifest);
    }

    public static T? DeserializeManifest<T>(string yaml) where T : class
    {
        var m = Deserializer.Deserialize<T>(yaml);
        if (m != null) NormalizeIncludedManifestPaths(m);
        return m;
    }

    /// <summary>
    /// Serializes a catalog (typically an IEnumerable&lt;CatalogItem&gt; wrapped
    /// in `items:`). makecatalogs writes one of these per catalog name.
    /// </summary>
    public static string SerializeCatalog<T>(T catalog) where T : class
        => Serializer.Serialize(catalog);

    public static T? DeserializeCatalog<T>(string yaml) where T : class
        => Deserializer.Deserialize<T>(yaml);

    /// <summary>
    /// Serializes InstallInfo.yaml — the state file managedsoftwareupdate writes
    /// at the end of each check phase and the MSC GUI reads.
    /// </summary>
    public static string SerializeInstallInfo(InstallInfoFile installInfo)
        => Serializer.Serialize(installInfo);

    public static InstallInfoFile? DeserializeInstallInfo(string yaml)
        => Deserializer.Deserialize<InstallInfoFile>(yaml);

    /// <summary>
    /// Extracts the `_metadata` block from raw YAML as a CLR dictionary,
    /// preserving the original key order. YamlDotNet 16.3 silently drops any
    /// `[YamlMember(Alias = "_metadata")]` binding (underscore-prefix aliases),
    /// so this helper exists as the canonical workaround — used by CimianStudio
    /// to round-trip _metadata that the strongly-typed model can't carry.
    /// </summary>
    public static Dictionary<string, object?>? ExtractMetadataBlock(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException)
        {
            return null;
        }

        if (stream.Documents.Count == 0) return null;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return null;

        foreach (var kvp in root.Children)
        {
            if (kvp.Key is YamlScalarNode keyScalar && keyScalar.Value == MetadataKey)
            {
                if (kvp.Value is YamlMappingNode metaMap)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var entry in metaMap.Children)
                    {
                        if (entry.Key is YamlScalarNode k && k.Value != null)
                        {
                            dict[k.Value] = ConvertNode(entry.Value);
                        }
                    }
                    return dict;
                }
                return null;
            }
        }
        return null;
    }

    // Converts a YamlNode to CLR primitives (string / dict / list) preserving
    // order. Mirrors the ConvertNode helper currently in CimianStudio's
    // PackageYamlSerializer (kept identical so the eventual deletion there is a
    // pure call-site swap).
    private static object? ConvertNode(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode s:
                return s.Value;
            case YamlSequenceNode seq:
                var list = new List<object?>();
                foreach (var item in seq.Children) list.Add(ConvertNode(item));
                return list;
            case YamlMappingNode map:
                var dict = new Dictionary<string, object?>();
                foreach (var entry in map.Children)
                {
                    if (entry.Key is YamlScalarNode k && k.Value != null)
                        dict[k.Value] = ConvertNode(entry.Value);
                }
                return dict;
            default:
                return null;
        }
    }

    // Reorders the top-level mapping of a serialized YAML document so the
    // priority keys come first (in the given order), then everything else
    // alphabetically, then the trailing key (e.g. _metadata) last if present.
    // We parse via YamlStream to avoid line-level string surgery — comments
    // would be lost either way, but YamlStream preserves nested structure.
    private static string ReorderTopLevelKeys(string yaml, string[] priority, string trailing)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }
        if (stream.Documents.Count == 0) return yaml;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return yaml;

        var byKey = new Dictionary<string, KeyValuePair<YamlNode, YamlNode>>(StringComparer.Ordinal);
        foreach (var kvp in root.Children)
        {
            if (kvp.Key is YamlScalarNode k && k.Value != null)
            {
                // Skip empty-string scalar values (e.g. `description: ""`).
                // OmitNull doesn't catch these because `""` is not null, but
                // pre-consolidation cimiimport guarded every field with
                // `!string.IsNullOrEmpty(...)`. Suppress them here so the model
                // can keep non-nullable string defaults without leaking empty
                // keys into the output.
                if (kvp.Value is YamlScalarNode sv && string.IsNullOrEmpty(sv.Value))
                    continue;
                byKey[k.Value] = kvp;
            }
        }

        var ordered = new YamlMappingNode();
        foreach (var key in priority)
        {
            if (byKey.TryGetValue(key, out var kvp))
            {
                ordered.Add(kvp.Key, kvp.Value);
                byKey.Remove(key);
            }
        }

        var hasTrailing = byKey.Remove(trailing, out var trailingKvp);

        foreach (var key in byKey.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var kvp = byKey[key];
            ordered.Add(kvp.Key, kvp.Value);
        }

        if (hasTrailing)
        {
            ordered.Add(trailingKvp.Key, trailingKvp.Value);
        }

        var newDoc = new YamlDocument(ordered);
        var newStream = new YamlStream(newDoc);
        using var writer = new StringWriter();
        newStream.Save(writer, assignAnchors: false);
        // YamlStream.Save emits CRLF on Windows and appends a `...` document-
        // end marker — both unwanted. Normalize to `\n` and strip the marker
        // so output matches the rest of the Cimian YAML corpus (no file in
        // deployment/ has CRLF or `...`).
        var result = writer.ToString().Replace("\r\n", "\n");
        if (result.EndsWith("...\n", StringComparison.Ordinal))
            result = result.Substring(0, result.Length - 4);
        else if (result.EndsWith("...", StringComparison.Ordinal))
            result = result.Substring(0, result.Length - 3);
        // Ensure exactly one trailing newline.
        result = result.TrimEnd('\n') + "\n";
        return result;
    }

    // Walks the direct string properties of `obj`, normalizing CRLF to LF and
    // collapsing sequences of three or more consecutive blank lines to two.
    // Keeps multiline `|` blocks clean across Windows/Linux checkouts.
    // Note: only processes the immediate object's properties — nested objects
    // (e.g. List<InstallItem>) are not walked, but PkgsInfo script properties
    // are all flat so this is sufficient for all current callers.
    private static void NormalizeMultilineStrings(object? obj)
    {
        if (obj == null) return;
        var type = obj.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.PropertyType == typeof(string))
            {
                if (prop.GetValue(obj) is string s && (s.Contains('\r') || s.Contains("\n\n\n")))
                {
                    var normalized = s.Replace("\r\n", "\n").Replace("\r", "\n");
                    while (normalized.Contains("\n\n\n"))
                        normalized = normalized.Replace("\n\n\n", "\n\n");
                    prop.SetValue(obj, normalized);
                }
            }
        }
    }

    // Reflection-based normalization for any model with an
    // IncludedManifests : List<string> property. Avoids creating a marker
    // interface that every manifest model would have to implement.
    private static void NormalizeIncludedManifestPaths(object obj)
    {
        var prop = obj.GetType().GetProperty("IncludedManifests", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.GetValue(obj) is IList list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is string s) list[i] = s.Replace('\\', '/');
            }
        }
    }

    /// <summary>
    /// Forces literal `|` style for any scalar containing a newline. YamlDotNet
    /// defaults to `>` (folded) for multiline strings, which collapses single
    /// newlines into spaces — fatal for embedded PowerShell scripts.
    /// </summary>
    private sealed class LiteralMultilineEmitter : ChainedEventEmitter
    {
        public LiteralMultilineEmitter(IEventEmitter next) : base(next) { }

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(string)
                && eventInfo.Source.Value is string s
                && s.Contains('\n'))
            {
                eventInfo = new ScalarEventInfo(eventInfo.Source)
                {
                    Style = ScalarStyle.Literal,
                    IsPlainImplicit = false,
                    IsQuotedImplicit = false,
                };
            }
            base.Emit(eventInfo, emitter);
        }
    }
}
