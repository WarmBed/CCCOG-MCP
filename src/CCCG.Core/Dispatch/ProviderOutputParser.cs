using System.Text;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

/// <summary>
/// Whether a provider's raw stdout stream already carries a terminal outcome
/// (the provider itself reported completion or failure), independent of
/// whether the CCCG dispatch worker process that launched it is still alive
/// to record that outcome. Used by the dead-worker reconciler: a stuck
/// "running" job whose worker died can still be resolved correctly by
/// reading what the provider actually said before the worker was killed.
/// </summary>
public readonly record struct TerminalOutcome(bool IsTerminal, bool Succeeded, string? Error)
{
    public static readonly TerminalOutcome NotTerminal = new(false, false, null);

    public static TerminalOutcome Success() => new(true, true, null);

    public static TerminalOutcome Failure(string? error) => new(true, false, error);
}

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

    /// <summary>
    /// Dead-worker reconciler support: inspect the stdout tail for a
    /// terminal marker the provider already emitted before the CCCG worker
    /// process was found dead. Reads only the last <paramref name="tailBytes"/>
    /// of the file (the marker is always the newest line for the JSONL
    /// providers), so this stays cheap even against multi-megabyte streams
    /// from long xhigh reasoning turns.
    /// </summary>
    public static TerminalOutcome FindTerminalOutcome(
        string provider,
        string stdoutPath,
        int tailBytes = 131072)
    {
        if (!File.Exists(stdoutPath))
        {
            return TerminalOutcome.NotTerminal;
        }

        if (provider.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in ReadTailLines(stdoutPath, tailBytes))
            {
                if (!TryParseLine(line, out var root)
                    || !root.TryGetProperty("type", out var type))
                {
                    continue;
                }

                var typeName = type.GetString();
                if (string.Equals(typeName, "turn.completed", StringComparison.Ordinal))
                {
                    return TerminalOutcome.Success();
                }

                // codex exec --json has no confirmed failure-marker schema in
                // this codebase's evidence; these two are checked defensively
                // in case a future/older CLI emits them, but the primary
                // signal remains turn.completed presence/absence.
                if (string.Equals(typeName, "turn.failed", StringComparison.Ordinal)
                    || string.Equals(typeName, "error", StringComparison.Ordinal))
                {
                    var message = root.TryGetProperty("message", out var messageValue)
                        && messageValue.ValueKind == JsonValueKind.String
                            ? messageValue.GetString()
                            : null;
                    return TerminalOutcome.Failure(message ?? "Codex reported a turn failure.");
                }
            }

            return TerminalOutcome.NotTerminal;
        }

        if (provider.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in ReadTailLines(stdoutPath, tailBytes))
            {
                if (!TryParseLine(line, out var root)
                    || !root.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "result", StringComparison.Ordinal))
                {
                    continue;
                }

                var isError = root.TryGetProperty("is_error", out var errorValue)
                    && errorValue.ValueKind is JsonValueKind.True;
                var message = root.TryGetProperty("result", out var resultValue)
                    && resultValue.ValueKind == JsonValueKind.String
                        ? resultValue.GetString()
                        : null;
                return isError
                    ? TerminalOutcome.Failure(message ?? "Claude returned an error result.")
                    : TerminalOutcome.Success();
            }

            return TerminalOutcome.NotTerminal;
        }

        if (provider.Equals("grok", StringComparison.OrdinalIgnoreCase))
        {
            // grok's CLI writes one JSON object at the very end of the turn,
            // not JSONL, so a parseable object with a "text" field is itself
            // the terminal marker.
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(stdoutPath));
                if (document.RootElement.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    return TerminalOutcome.Success();
                }
            }
            catch (JsonException)
            {
            }

            return TerminalOutcome.NotTerminal;
        }

        return TerminalOutcome.NotTerminal;
    }

    /// <summary>
    /// Reads the last <paramref name="tailBytes"/> of a file and returns its
    /// non-empty lines newest-first, so callers scanning for a terminal
    /// marker (always the most recent JSONL line) can stop at the first hit.
    /// A partial first line from seeking mid-file is dropped.
    /// </summary>
    private static IEnumerable<string> ReadTailLines(string path, int tailBytes)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        var start = Math.Max(0, length - tailBytes);
        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        var rawLines = text.Split('\n');
        var lines = new List<string>(rawLines.Length);
        for (var index = start > 0 ? 1 : 0; index < rawLines.Length; index++)
        {
            var line = rawLines[index].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        lines.Reverse();
        return lines;
    }

    private static bool TryParseLine(string line, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[^maxChars..];
}
