using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Sentinel.Gateway.Http;

/// <summary>
/// JSON written by the gateway itself (OpenAI-shaped responses and errors). Non-ASCII text is written as is instead of
/// <c>\uXXXX</c> (Turkish answers stay readable and a third smaller); HTML-sensitive characters stay escaped.
/// </summary>
internal static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(Options);
}
