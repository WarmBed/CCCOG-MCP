using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCCG.Core.Monitoring;

public sealed record ParsedTranscriptContent
{
    public required string Kind { get; init; }

    public required string Text { get; init; }

    public int BlockIndex { get; init; }

    public DateTimeOffset? SourceTime { get; init; }

    public string? RawEntryId { get; init; }

    public string? RawParentEntryId { get; init; }

    public string? RawPromptId { get; init; }

    public string? RawRequestId { get; init; }

    public string? RawProviderMessageId { get; init; }

    public string? Model { get; init; }

    public bool IsSidechain { get; init; }
}

public sealed record MonitorContentRecord
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sourceTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SourceTime { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("sessionKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionKey { get; init; }

    [JsonPropertyName("cliSessionKey")]
    public required string CliSessionKey { get; init; }

    [JsonPropertyName("entryKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryKey { get; init; }

    [JsonPropertyName("logicalRecordKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalRecordKey { get; init; }

    [JsonPropertyName("parentEntryKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentEntryKey { get; init; }

    [JsonPropertyName("promptKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptKey { get; init; }

    [JsonPropertyName("requestKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestKey { get; init; }

    [JsonPropertyName("providerMessageKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderMessageKey { get; init; }

    [JsonPropertyName("blockIndex")]
    public int BlockIndex { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("isSidechain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSidechain { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class TranscriptContentParser
{
    public IReadOnlyList<ParsedTranscriptContent> Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryString(root, "type", out var type)
                || type is not ("user" or "assistant")
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !TryString(message, "role", out var role)
                || role != type
                || !message.TryGetProperty("content", out var content))
            {
                return Array.Empty<ParsedTranscriptContent>();
            }

            var textBlocks = TextBlocks(content);
            if (textBlocks.Count == 0)
            {
                return Array.Empty<ParsedTranscriptContent>();
            }

            var kind = type == "user" ? "user_prompt" : "assistant_response";
            DateTimeOffset? sourceTime = TryString(root, "timestamp", out var timestamp)
                && DateTimeOffset.TryParse(timestamp, out var parsedTime)
                    ? parsedTime
                    : null;
            var isSidechain = root.TryGetProperty("isSidechain", out var sidechain)
                && sidechain.ValueKind is JsonValueKind.True;

            var result = new List<ParsedTranscriptContent>(textBlocks.Count);
            foreach (var block in textBlocks)
            {
                result.Add(new ParsedTranscriptContent
                {
                    Kind = kind,
                    Text = block.Text,
                    BlockIndex = block.Index,
                    SourceTime = sourceTime,
                    RawEntryId = StringOrNull(root, "uuid"),
                    RawParentEntryId = StringOrNull(root, "parentUuid"),
                    RawPromptId = StringOrNull(root, "promptId"),
                    RawRequestId = StringOrNull(root, "requestId"),
                    RawProviderMessageId = StringOrNull(message, "id"),
                    Model = StringOrNull(message, "model"),
                    IsSidechain = isSidechain
                });
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<ParsedTranscriptContent>();
        }
    }

    private static IReadOnlyList<(int Index, string Text)> TextBlocks(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            return string.IsNullOrEmpty(text)
                ? Array.Empty<(int, string)>()
                : new[] { (0, text) };
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<(int, string)>();
        }

        var blocks = new List<(int, string)>();
        var index = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && TryString(item, "type", out var itemType)
                && itemType == "text"
                && TryString(item, "text", out var text)
                && text.Length > 0)
            {
                blocks.Add((index, text));
            }

            index++;
        }

        return blocks;
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        TryString(element, property, out var value) ? value : null;

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var candidate)
            || candidate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = candidate.GetString() ?? string.Empty;
        return true;
    }
}
