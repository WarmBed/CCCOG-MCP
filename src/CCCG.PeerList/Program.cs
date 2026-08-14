using System.Text.Json;
using CCCG.Core.Dispatch;

var provider = args.Length > 0 ? args[0] : "codex";
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

PeerList listed = provider.Equals("grok", StringComparison.OrdinalIgnoreCase)
    ? new GrokPeerDirectory(GrokHome.Resolve()).List()
    : new CodexPeerDirectory(CodexHome.Resolve()).List();

Console.WriteLine(JsonSerializer.Serialize(listed, options));
