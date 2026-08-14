using System.Text.Json;

namespace CCCG.Core.Monitoring;

public sealed record ParsedTranscriptOperation
{
    public required string Event { get; init; }

    public DateTimeOffset? SourceTime { get; init; }

    public string? RawOperationId { get; init; }

    public string? ToolName { get; init; }

    public string? ToolKind { get; init; }

    public IReadOnlyList<string> InputKeys { get; init; } = Array.Empty<string>();

    public int PayloadCharacters { get; init; }

    public int ResultBlockCount { get; init; }

    public bool? IsError { get; init; }

    public int? HookCount { get; init; }

    public int? HookInfoCount { get; init; }

    public int? HookErrorCount { get; init; }

    public bool? PreventedContinuation { get; init; }

    public bool? HasOutput { get; init; }

    public string? StopReason { get; init; }

    public bool IsSidechain { get; init; }
}

public sealed class TranscriptOperationParser
{
    public IReadOnlyList<ParsedTranscriptOperation> Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryString(root, "type", out var type))
            {
                return Array.Empty<ParsedTranscriptOperation>();
            }

            var sourceTime = SourceTime(root);
            var isSidechain = BooleanOrNull(root, "isSidechain") == true;
            if (type == "assistant")
            {
                return ParseToolRequests(root, sourceTime, isSidechain);
            }

            if (type == "user")
            {
                return ParseToolResults(root, sourceTime, isSidechain);
            }

            if (type == "system"
                && TryString(root, "subtype", out var subtype)
                && subtype == "stop_hook_summary")
            {
                return new[] { ParseHookSummary(root, sourceTime) };
            }

            return Array.Empty<ParsedTranscriptOperation>();
        }
        catch (JsonException)
        {
            return Array.Empty<ParsedTranscriptOperation>();
        }
    }

    public static string ClassifyTool(string name)
    {
        if (name.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
        {
            return "web_search";
        }

        if (name.Equals("WebFetch", StringComparison.OrdinalIgnoreCase))
        {
            return "web_fetch";
        }

        if (name.StartsWith("mcp__ccd_session_mgmt__", StringComparison.OrdinalIgnoreCase))
        {
            return "cross_session";
        }

        if (name.StartsWith("mcp__Claude_Browser__", StringComparison.OrdinalIgnoreCase))
        {
            return "browser";
        }

        if (name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp";
        }

        if (name is "Bash" or "PowerShell")
        {
            return "shell";
        }

        if (name is "Read" or "Edit" or "Write" or "Grep" or "Glob")
        {
            return "filesystem";
        }

        return "other";
    }

    private static IReadOnlyList<ParsedTranscriptOperation> ParseToolRequests(
        JsonElement root,
        DateTimeOffset? sourceTime,
        bool isSidechain)
    {
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ParsedTranscriptOperation>();
        }

        var result = new List<ParsedTranscriptOperation>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryString(item, "type", out var itemType)
                || itemType != "tool_use"
                || !TryString(item, "name", out var name))
            {
                continue;
            }

            var inputKeys = Array.Empty<string>();
            var payloadCharacters = 0;
            if (item.TryGetProperty("input", out var input))
            {
                payloadCharacters = input.GetRawText().Length;
                if (input.ValueKind == JsonValueKind.Object)
                {
                    inputKeys = input.EnumerateObject().Select(property => property.Name).ToArray();
                }
            }

            result.Add(new ParsedTranscriptOperation
            {
                Event = "session.tool_requested",
                SourceTime = sourceTime,
                RawOperationId = StringOrNull(item, "id"),
                ToolName = name,
                ToolKind = ClassifyTool(name),
                InputKeys = inputKeys,
                PayloadCharacters = payloadCharacters,
                IsSidechain = isSidechain
            });
        }

        return result;
    }

    private static IReadOnlyList<ParsedTranscriptOperation> ParseToolResults(
        JsonElement root,
        DateTimeOffset? sourceTime,
        bool isSidechain)
    {
        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ParsedTranscriptOperation>();
        }

        var result = new List<ParsedTranscriptOperation>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryString(item, "type", out var itemType)
                || itemType != "tool_result")
            {
                continue;
            }

            var payloadCharacters = 0;
            var blockCount = 0;
            if (item.TryGetProperty("content", out var resultContent))
            {
                payloadCharacters = resultContent.GetRawText().Length;
                blockCount = resultContent.ValueKind == JsonValueKind.Array
                    ? resultContent.GetArrayLength()
                    : resultContent.ValueKind == JsonValueKind.Null ? 0 : 1;
            }

            result.Add(new ParsedTranscriptOperation
            {
                Event = "session.tool_completed",
                SourceTime = sourceTime,
                RawOperationId = StringOrNull(item, "tool_use_id"),
                PayloadCharacters = payloadCharacters,
                ResultBlockCount = blockCount,
                IsError = BooleanOrNull(item, "is_error") ?? false,
                IsSidechain = isSidechain
            });
        }

        return result;
    }

    private static ParsedTranscriptOperation ParseHookSummary(
        JsonElement root,
        DateTimeOffset? sourceTime) =>
        new()
        {
            Event = "session.hook_summary",
            SourceTime = sourceTime,
            HookCount = IntegerOrNull(root, "hookCount"),
            HookInfoCount = ArrayCountOrNull(root, "hookInfos"),
            HookErrorCount = ArrayCountOrNull(root, "hookErrors"),
            PreventedContinuation = BooleanOrNull(root, "preventedContinuation"),
            HasOutput = BooleanOrNull(root, "hasOutput"),
            StopReason = StringOrNull(root, "stopReason")
        };

    private static DateTimeOffset? SourceTime(JsonElement root) =>
        TryString(root, "timestamp", out var timestamp)
        && DateTimeOffset.TryParse(timestamp, out var parsed)
            ? parsed
            : null;

    private static int? IntegerOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static int? ArrayCountOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : null;

    private static bool? BooleanOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
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
