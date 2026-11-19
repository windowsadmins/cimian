using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Cimian.Core.Services;

/// <summary>
/// Reads repository files written as XML property lists with the same keys as the
/// YAML form. A plist is converted to the YAML text the existing code reads (YamlUtils
/// does this in its Deserialize methods when the text looks like a plist), so the typed
/// models, the key order and the _metadata handling stay as they are.
///
/// Supported elements are dict, array, string, integer, real, true, false and date. A
/// data element and a binary plist (bplist00) are refused. The Apple DOCTYPE is
/// ignored and never fetched. A key repeated in a dict keeps its last value, as in
/// Apple's parser.
///
/// A string stays a string whatever it looks like ("1.10", "true", "null"), and a value
/// under one of the TextKeys given as an integer, real, true or false is read as its
/// text. Dates are UTC, written as yyyy-MM-ddTHH:mm:ssZ.
/// </summary>
public static class PlistUtils
{
    internal const string AppleDoctype =
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">";

    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>
    /// Keys whose value is always text: a plist real 1.10 under version reads as the
    /// string "1.10", not the number 1.1.
    /// </summary>
    internal static IReadOnlySet<string> TextKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "version", "minimum_os_version", "maximum_os_version", "minimum_cimian_version",
    };

    /// <summary>
    /// The keys written first in the YAML made from a plist, then the other keys in the
    /// plist's (sorted) order, then _metadata. This is the order of Cimian's YAML writer,
    /// which readers that scan a file by line (DataExporter) rely on.
    /// </summary>
    private static readonly string[] LeadingKeys = { "name", "display_name", "version" };
    private const string MetadataKey = "_metadata";

    /// <summary>
    /// How deep a plist may nest (the same default as System.Text.Json). A deeper one
    /// is refused with a message instead of being parsed on a stack the text controls.
    /// </summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// True when the text is an XML plist rather than YAML: after spaces, tabs, line breaks
    /// or a byte-order mark it begins with the plist DOCTYPE or root element, or with an
    /// XML declaration and one of them within the first <see cref="PlistHeadWindow"/>
    /// characters.
    /// Other XML (a web.config) is not a plist, and neither is a binary plist
    /// (see <see cref="IsBinaryPlist(string)"/>).
    /// </summary>
    public static bool LooksLikePlist(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var head = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith("<!DOCTYPE plist", StringComparison.Ordinal)
            || head.StartsWith("<plist", StringComparison.Ordinal))
        {
            return true;
        }
        if (!head.StartsWith("<?xml", StringComparison.Ordinal))
            return false;
        var window = head.Length > PlistHeadWindow ? head[..PlistHeadWindow] : head;
        return window.Contains("<!DOCTYPE plist", StringComparison.Ordinal)
            || window.Contains("<plist", StringComparison.Ordinal);
    }

    /// <summary>How far after an XML declaration the plist DOCTYPE or root element is looked for.</summary>
    internal const int PlistHeadWindow = 1024;

    public static bool IsBinaryPlist(string? text)
        => text != null && text.StartsWith("bplist00", StringComparison.Ordinal);

    /// <summary>
    /// The value tree of an XML plist: Dictionary&lt;string, object?&gt; for
    /// dict, List&lt;object?&gt; for array, string, long, double, bool, and
    /// DateTime (Kind Utc) for date. Throws <see cref="PlistFormatException"/>
    /// for anything else.
    /// </summary>
    internal static object? Parse(string xml)
    {
        if (IsBinaryPlist(xml))
            throw new PlistFormatException("A binary plist (bplist00): only XML plists are read");
        if (!LooksLikePlist(xml))
            throw new PlistFormatException("Not an XML plist: the text does not begin with <?xml, <!DOCTYPE plist or <plist");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
        };
        // A byte-order mark or white space before the declaration is not valid XML,
        // but hand-written files have it.
        xml = xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        XDocument doc;
        try
        {
            // Check the depth while streaming, before a tree is built: the walks below
            // (and XElement.Value) recurse once per level.
            using (var probe = XmlReader.Create(new StringReader(xml), settings))
            {
                while (probe.Read())
                {
                    if (probe.NodeType == XmlNodeType.Element && probe.Depth > MaxDepth)
                        throw new PlistFormatException($"The plist nests deeper than {MaxDepth} levels");
                }
            }
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new PlistFormatException($"The plist is not well-formed XML: {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new PlistFormatException("The plist has no root element");
        if (root.Name.LocalName != "plist")
            throw new PlistFormatException($"The root element is <{root.Name.LocalName}>, not <plist>");
        var children = root.Elements().ToList();
        if (children.Count != 1)
            throw new PlistFormatException($"<plist> must hold exactly one value, not {children.Count}");
        return ParseElement(children[0], "/");
    }

    private static object? ParseElement(XElement element, string path)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
                return ParseDict(element, path);
            case "array":
                return element.Elements().Select((child, index) => ParseElement(child, $"{path}[{index}]")).ToList();
            case "string":
                return element.Value;
            case "integer":
                if (!long.TryParse(element.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    throw new PlistFormatException($"{path}: <integer> holds '{element.Value}', not an integer");
                return l;
            case "real":
                if (!double.TryParse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new PlistFormatException($"{path}: <real> holds '{element.Value}', not a number");
                return d;
            case "true":
                return true;
            case "false":
                return false;
            case "date":
                return ParseDate(element.Value.Trim(), path);
            case "data":
                throw new PlistFormatException($"{path}: <data> has no place in a Cimian file (no key takes bytes)");
            default:
                throw new PlistFormatException($"{path}: unknown element <{element.Name.LocalName}>");
        }
    }

    private static Dictionary<string, object?> ParseDict(XElement element, string path)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? pendingKey = null;
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "key")
            {
                if (pendingKey != null)
                    throw new PlistFormatException($"{path}: the key '{pendingKey}' has no value");
                pendingKey = child.Value;
                continue;
            }
            if (pendingKey == null)
                throw new PlistFormatException($"{path}: a value without a <key> before it");
            // A repeated key keeps its last value, as in Apple's parser and plistlib.
            // A value under a text key is text whatever element holds it (a real 1.10
            // under version is "1.10", not 1.1).
            var tag = child.Name.LocalName;
            if (TextKeys.Contains(pendingKey) && tag is "integer" or "real")
                result[pendingKey] = child.Value.Trim();
            else if (TextKeys.Contains(pendingKey) && tag is "true" or "false")
                result[pendingKey] = tag;
            else
                result[pendingKey] = ParseElement(child, $"{path}{pendingKey}/");
            pendingKey = null;
        }
        if (pendingKey != null)
            throw new PlistFormatException($"{path}: the key '{pendingKey}' has no value");
        return result;
    }

    // ISO 8601 only, as Apple's parser writes it; also a fraction, an offset, no zone
    // (read as UTC) or a date alone. 02/12/2026 is refused rather than guessed.
    private static readonly string[] DateFormats =
    {
        DateFormat, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd",
    };

    private static DateTime ParseDate(string text, string path)
    {
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        throw new PlistFormatException($"{path}: <date> holds '{text}', not a date");
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// The YAML text the existing readers parse, for a plist of the given kind. A
    /// catalog that is a bare array is wrapped as items. Strings keep their text,
    /// including ones that look like numbers or booleans; integers, booleans and dates
    /// keep their type.
    /// </summary>
    public static string ToYaml(string plistXml, RepoFileKind kind)
    {
        var tree = Parse(plistXml);
        if (kind == RepoFileKind.Catalog && tree is List<object?> items)
            tree = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = items };
        if (tree is not Dictionary<string, object?>)
            throw new PlistFormatException($"A {KindName(kind)} plist must hold a dictionary" +
                                           (kind == RepoFileKind.Catalog ? " or an array of items" : string.Empty));
        var root = ToYamlNode(tree, parentKey: null, depth: 0);
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var text = writer.ToString().Replace("\r\n", "\n");
        if (text.EndsWith("...\n", StringComparison.Ordinal))
            text = text[..^4];
        else if (text.EndsWith("...", StringComparison.Ordinal))
            text = text[..^3];
        return text.EndsWith('\n') ? text : text + "\n";
    }

    private static YamlNode ToYamlNode(object? value, string? parentKey, int depth)
    {
        if (depth > MaxDepth)
            throw new PlistFormatException($"The plist nests deeper than {MaxDepth} levels");
        switch (value)
        {
            case Dictionary<string, object?> dict:
                {
                    var mapping = new YamlMappingNode();
                    foreach (var key in InCimianOrder(dict.Keys))
                        mapping.Add(KeyNode(key), ToYamlNode(dict[key], key, depth + 1));
                    return mapping;
                }
            case List<object?> list:
                {
                    var sequence = new YamlSequenceNode();
                    foreach (var item in list) sequence.Add(ToYamlNode(item, parentKey, depth + 1));
                    return sequence;
                }
            case string s:
                return TextNode(s);
            case bool b when parentKey != null && TextKeys.Contains(parentKey):
                return TextNode(b ? "true" : "false");
            case long l when parentKey != null && TextKeys.Contains(parentKey):
                return TextNode(l.ToString(CultureInfo.InvariantCulture));
            case double d when parentKey != null && TextKeys.Contains(parentKey):
                return TextNode(d.ToString("R", CultureInfo.InvariantCulture));
            case bool b:
                return new YamlScalarNode(b ? "true" : "false") { Style = ScalarStyle.Plain };
            case long l:
                return new YamlScalarNode(l.ToString(CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain };
            case double d:
                return new YamlScalarNode(d.ToString("R", CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain };
            case DateTime dt:
                return new YamlScalarNode(ToUtc(dt).ToString(DateFormat, CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain };
            default:
                throw new PlistFormatException($"A value of type {value?.GetType().Name ?? "null"} cannot be written as YAML");
        }
    }

    // Quote a string that a YAML 1.1 reader would take for a number, boolean or null,
    // or that holds characters with a meaning in YAML; a multi-line string is a literal
    // block. Use double quotes when the text holds an apostrophe, so that readers that
    // strip only the outer quotes of a line see the text unchanged. Double quotes also
    // keep the line-break characters that YAML would fold or drop: CR, NEL, LS and PS,
    // and a string of line breaks only.
    private static YamlNode TextNode(string s)
    {
        if (s.IndexOfAny(new[] { '\r', '\u0085', '\u2028', '\u2029' }) >= 0 || (s.Contains('\n') && s.Trim('\n').Length == 0))
            return new YamlScalarNode(s) { Style = ScalarStyle.DoubleQuoted };
        if (s.Contains('\n'))
            return new YamlScalarNode(s) { Style = ScalarStyle.Literal };
        if (!NeedsQuotes(s))
            return new YamlScalarNode(s) { Style = ScalarStyle.Plain };
        return new YamlScalarNode(s) { Style = s.Contains('\'') ? ScalarStyle.DoubleQuoted : ScalarStyle.SingleQuoted };
    }

    // Identifier keys stay plain; anything else is double-quoted so YAML reads it back as the same text.
    private static YamlNode KeyNode(string key)
        => new YamlScalarNode(key)
        {
            Style = key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && !NeedsQuotes(key)
                ? ScalarStyle.Plain
                : ScalarStyle.DoubleQuoted,
        };

    // name, display_name, version first (when present), _metadata last, the
    // rest in the order given (a plist's keys are sorted).
    private static IEnumerable<string> InCimianOrder(IEnumerable<string> keys)
    {
        var all = keys.ToList();
        foreach (var lead in LeadingKeys)
        {
            if (all.Contains(lead))
                yield return lead;
        }
        foreach (var key in all)
        {
            if (!LeadingKeys.Contains(key) && key != MetadataKey)
                yield return key;
        }
        if (all.Contains(MetadataKey))
            yield return MetadataKey;
    }

    // Quote a scalar that a YAML 1.1 reader would take for a number, boolean or null,
    // or that holds characters with a meaning in YAML. Over-quoting is harmless;
    // under-quoting changes the type.
    private static bool NeedsQuotes(string s)
    {
        if (s.Length == 0)
            return true;
        if (s != s.Trim())
            return true;
        if (LooksLikeNumber(s) || LooksLikeBoolOrNull(s))
            return true;
        if (s.Contains('\'') || s.Contains('"'))
            return true;
        if (s.StartsWith('-') || s.StartsWith('?') || s.StartsWith(':') || s.StartsWith('!') || s.StartsWith('&')
            || s.StartsWith('*') || s.StartsWith('|') || s.StartsWith('>') || s.StartsWith('%') || s.StartsWith('@')
            || s.StartsWith('`') || s.StartsWith('#') || s.StartsWith('[') || s.StartsWith('{') || s.StartsWith(',')
            || s.StartsWith('\\'))
            return true;
        if (s.Contains(": ") || s.Contains(" #") || s.EndsWith(':'))
            return true;
        if (s.Contains('\t') || s.Contains('\r'))
            return true;
        return false;
    }

    private static bool LooksLikeNumber(string s)
    {
        var t = s;
        if (t.StartsWith('+') || t.StartsWith('-'))
            t = t[1..];
        if (t.Length == 0)
            return false;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0o", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(t, ".inf", StringComparison.OrdinalIgnoreCase) || string.Equals(t, ".nan", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.All(c => char.IsDigit(c) || c == '_'))
            return true;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return true;
        // 1_000 and sexagesimal 1:30 are YAML 1.1 numbers
        if (t.All(c => char.IsDigit(c) || c == '_' || c == ':' || c == '.'))
            return true;
        return false;
    }

    private static bool LooksLikeBoolOrNull(string s)
    {
        switch (s.ToLowerInvariant())
        {
            case "true": case "false": case "yes": case "no": case "on": case "off": case "y": case "n":
            case "null": case "~": case "":
                return true;
            default:
                return false;
        }
    }

    private static string KindName(RepoFileKind kind) => kind switch
    {
        RepoFileKind.PkgInfo => "pkginfo",
        RepoFileKind.Manifest => "manifest",
        _ => "catalog",
    };
}

/// <summary>A plist that cannot be converted.</summary>
public sealed class PlistFormatException : FormatException
{
    public PlistFormatException(string message) : base(message) { }
    public PlistFormatException(string message, Exception inner) : base(message, inner) { }
}
