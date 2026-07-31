using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Trading.SampleBot;

internal static class SampleBotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
