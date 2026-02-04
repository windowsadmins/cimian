using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Cimian.CLI.Makecatalogs.Services;

/// <summary>
/// Custom event emitter that forces literal style (|) for multiline strings
/// instead of folded style (>) which YamlDotNet uses by default.
/// This preserves exact line breaks as they appear in the source YAML.
/// </summary>
public class LiteralMultilineEmitter : ChainedEventEmitter
{
    public LiteralMultilineEmitter(IEventEmitter nextEmitter) : base(nextEmitter)
    {
    }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        // Check if the value is a string containing newlines
        if (eventInfo.Source.Type == typeof(string) && eventInfo.Source.Value is string strValue)
        {
            if (strValue.Contains('\n'))
            {
                // Force literal style (|) for multiline strings
                eventInfo = new ScalarEventInfo(eventInfo.Source)
                {
                    Style = ScalarStyle.Literal,
                    IsPlainImplicit = false,
                    IsQuotedImplicit = false
                };
            }
        }

        base.Emit(eventInfo, emitter);
    }
}
