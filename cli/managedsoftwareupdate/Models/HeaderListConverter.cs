using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Cimian.CLI.managedsoftwareupdate.Models;

/// <summary>
/// Reads the AdditionalHttpHeaders setting, a list of "Name: value" strings. The other natural
/// spellings are not strings in YAML: an unquoted entry is a one-key mapping, headers written
/// as names and values are a mapping, and a lone entry is a scalar. Each would fail the whole
/// Config.yaml or drop the headers, so each is read as the same entries, in document order.
/// An empty key is no entries. Applied to a property by attribute and registered with the
/// builders, which look the instance up by its type.
/// </summary>
public class HeaderListConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => false;

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var entries = new List<string>();

        // A lone string is one entry. An empty key is YAML null and means no entries.
        if (parser.Accept<Scalar>(out _))
        {
            if (rootDeserializer(typeof(string)) is string entry)
            {
                entries.Add(entry);
            }

            return entries;
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                entries.Add(ReadPair(parser));
            }

            return entries;
        }

        parser.Consume<SequenceStart>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            if (parser.TryConsume<MappingStart>(out _))
            {
                entries.Add(ReadPair(parser));
                parser.Consume<MappingEnd>();
            }
            else
            {
                entries.Add(parser.Consume<Scalar>().Value);
            }
        }

        return entries;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        foreach (var entry in value as List<string> ?? [])
        {
            emitter.Emit(new Scalar(null, null, entry, ScalarStyle.DoubleQuoted, false, true));
        }
        emitter.Emit(new SequenceEnd());
    }

    private static string ReadPair(IParser parser)
    {
        var name = parser.Consume<Scalar>().Value;
        var value = parser.Consume<Scalar>().Value;
        return $"{name}: {value}";
    }
}
