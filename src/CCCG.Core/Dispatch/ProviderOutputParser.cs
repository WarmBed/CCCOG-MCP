using System.Text.Json;

namespace CCCG.Core.Dispatch;

public static class ProviderOutputParser
{
    public static string? FindSessionId(string provider, string stdoutPath, string? plannedId = null)
    {
        if (!string.IsNullOrWhiteSpace(plannedId))
        {
            return plannedId;
        }

        if (!File.Exists(stdoutPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(stdoutPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (provider.Equals("codex", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "thread.started", StringComparison.Ordinal)
                    && root.TryGetProperty("thread_id", out var threadId)
                    && threadId.ValueKind == JsonValueKind.String)
                {
                    return threadId.GetString();
                }

                if (provider.Equals("claude", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("session_id", out var sessionId)
                    && sessionId.ValueKind == JsonValueKind.String)
                {
                    return sessionId.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    public static string CollectResponse(string provider, string stdoutPath, int maxChars = 8000)
    {
        if (!File.Exists(stdoutPath))
        {
            return string.Empty;
        }

        if (provider.Equals("grok", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(stdoutPath));
                var root = document.RootElement;
                if (root.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    return Truncate(text.GetString() ?? string.Empty, maxChars);
                }
            }
            catch (JsonException)
            {
            }
        }

        if (provider.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            var messages = new List<string>();
            foreach (var line in File.ReadLines(stdoutPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var type)
                        && string.Equals(type.GetString(), "item.completed", StringComparison.Ordinal)
                        && root.TryGetProperty("item", out var item)
                        && item.TryGetProperty("type", out var itemType)
                        && string.Equals(itemType.GetString(), "agent_message", StringComparison.Ordinal)
                        && item.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        messages.Add(text.GetString()!);
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (messages.Count > 0)
            {
                return Truncate(string.Join(Environment.NewLine, messages), maxChars);
            }
        }

        if (provider.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            string? result = null;
            var messages = new List<string>();
            foreach (var line in File.ReadLines(stdoutPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var type)
                        && string.Equals(type.GetString(), "result", StringComparison.Ordinal)
                        && root.TryGetProperty("result", out var resultValue)
                        && resultValue.ValueKind == JsonValueKind.String)
                    {
                        result = resultValue.GetString();
                    }
                    else if (root.TryGetProperty("type", out type)
                        && string.Equals(type.GetString(), "assistant", StringComparison.Ordinal)
                        && root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var blockType)
                                && blockType.GetString() == "text"
                                && block.TryGetProperty("text", out var text)
                                && text.ValueKind == JsonValueKind.String)
                            {
                                messages.Add(text.GetString()!);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return Truncate(result, maxChars);
            }

            if (messages.Count > 0)
            {
                return Truncate(string.Join(Environment.NewLine, messages), maxChars);
            }
        }

        return Truncate(File.ReadAllText(stdoutPath), maxChars);
    }

    public static string? FindError(string provider, string stdoutPath)
    {
        if (!provider.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(stdoutPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(stdoutPath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "result"
                    && root.TryGetProperty("is_error", out var isError)
                    && isError.ValueKind is JsonValueKind.True)
                {
                    return root.TryGetProperty("result", out var result)
                        && result.ValueKind == JsonValueKind.String
                            ? result.GetString() ?? "Claude returned an error result."
                            : "Claude returned an error result.";
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];
}
