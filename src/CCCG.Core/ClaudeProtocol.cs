using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCCG.Core;

public enum ClaudeFrameKind
{
    Unknown,
    User,
    SetModel,
    Interrupt
}

public sealed record ClaudeInputFrame(
    ClaudeFrameKind Kind,
    string? Text = null,
    string? Model = null,
    string? RequestId = null,
    string? SessionId = null);

public static class ClaudeProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static ClaudeInputFrame ParseInput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new ClaudeInputFrame(ClaudeFrameKind.Unknown);
        }

        line = line.TrimStart('\uFEFF');

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return new ClaudeInputFrame(ClaudeFrameKind.Unknown);
        }

        if (root is not JsonObject frame)
        {
            return new ClaudeInputFrame(ClaudeFrameKind.Unknown);
        }

        var type = ReadString(frame["type"]);
        var sessionId = ReadString(frame["session_id"]);

        if (string.Equals(type, "user", StringComparison.Ordinal)
            && frame["message"] is JsonObject message
            && string.Equals(ReadString(message["role"]), "user", StringComparison.Ordinal))
        {
            var text = ExtractText(message["content"]);
            return text is null
                ? new ClaudeInputFrame(ClaudeFrameKind.Unknown, SessionId: sessionId)
                : new ClaudeInputFrame(ClaudeFrameKind.User, text, SessionId: sessionId);
        }

        if (string.Equals(type, "control_request", StringComparison.Ordinal)
            && frame["request"] is JsonObject request)
        {
            var requestId = ReadString(frame["request_id"]);
            var subtype = ReadString(request["subtype"]);

            if (string.Equals(subtype, "set_model", StringComparison.Ordinal))
            {
                return new ClaudeInputFrame(
                    ClaudeFrameKind.SetModel,
                    Model: ReadString(request["model"]),
                    RequestId: requestId,
                    SessionId: sessionId);
            }

            if (string.Equals(subtype, "interrupt", StringComparison.Ordinal))
            {
                return new ClaudeInputFrame(
                    ClaudeFrameKind.Interrupt,
                    RequestId: requestId,
                    SessionId: sessionId);
            }
        }

        return new ClaudeInputFrame(ClaudeFrameKind.Unknown, SessionId: sessionId);
    }

    public static string CreateInit(
        string sessionId,
        string cwd,
        string claudeModel,
        string permissionMode)
    {
        var frame = new
        {
            type = "system",
            subtype = "init",
            cwd,
            session_id = sessionId,
            tools = Array.Empty<string>(),
            mcp_servers = Array.Empty<object>(),
            model = claudeModel,
            permissionMode,
            slash_commands = Array.Empty<string>(),
            apiKeySource = "none",
            claude_code_version = "2.1.227",
            output_style = "default",
            agents = Array.Empty<string>(),
            skills = Array.Empty<string>(),
            plugins = Array.Empty<string>(),
            capabilities = new[] { "interrupt_receipt_v1" },
            analytics_disabled = true,
            product_feedback_disabled = true,
            uuid = Guid.NewGuid().ToString()
        };

        return JsonSerializer.Serialize(frame, JsonOptions);
    }

    public static string CreateAssistant(
        string sessionId,
        string claudeModel,
        string text,
        long inputTokens = 0,
        long outputTokens = 0)
    {
        var frame = new
        {
            type = "assistant",
            message = new
            {
                id = "msg_cccg_" + Guid.NewGuid().ToString("N"),
                type = "message",
                role = "assistant",
                model = claudeModel,
                content = new[] { new { type = "text", text } },
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = Usage(inputTokens, outputTokens)
            },
            parent_tool_use_id = (string?)null,
            uuid = Guid.NewGuid().ToString(),
            session_id = sessionId
        };

        return JsonSerializer.Serialize(frame, JsonOptions);
    }

    public static string CreateSuccessResult(
        string sessionId,
        string text,
        long durationMilliseconds,
        long inputTokens = 0,
        long outputTokens = 0)
    {
        var frame = new
        {
            type = "result",
            subtype = "success",
            duration_ms = durationMilliseconds,
            duration_api_ms = durationMilliseconds,
            is_error = false,
            num_turns = 1,
            result = text,
            stop_reason = (string?)null,
            total_cost_usd = 0,
            usage = Usage(inputTokens, outputTokens),
            modelUsage = new Dictionary<string, object>(),
            permission_denials = Array.Empty<object>(),
            uuid = Guid.NewGuid().ToString(),
            session_id = sessionId
        };

        return JsonSerializer.Serialize(frame, JsonOptions);
    }

    public static string CreateErrorResult(
        string sessionId,
        string error,
        long durationMilliseconds)
    {
        var frame = new
        {
            type = "result",
            subtype = "error_during_execution",
            duration_ms = durationMilliseconds,
            duration_api_ms = durationMilliseconds,
            is_error = true,
            num_turns = 0,
            stop_reason = (string?)null,
            total_cost_usd = 0,
            usage = Usage(0, 0),
            modelUsage = new Dictionary<string, object>(),
            permission_denials = Array.Empty<object>(),
            errors = new[] { error },
            uuid = Guid.NewGuid().ToString(),
            session_id = sessionId
        };

        return JsonSerializer.Serialize(frame, JsonOptions);
    }

    public static string CreateControlSuccess(string requestId, object? response = null)
    {
        var frame = new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = response ?? new { }
            }
        };

        return JsonSerializer.Serialize(frame, JsonOptions);
    }

    private static object Usage(long inputTokens, long outputTokens) => new
    {
        input_tokens = inputTokens,
        output_tokens = outputTokens,
        cache_creation_input_tokens = 0,
        cache_read_input_tokens = 0,
        server_tool_use = new { web_search_requests = 0 }
    };

    private static string? ExtractText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var direct))
        {
            return direct;
        }

        if (content is not JsonArray blocks)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var node in blocks)
        {
            if (node is JsonObject block
                && string.Equals(ReadString(block["type"]), "text", StringComparison.Ordinal)
                && ReadString(block["text"]) is { } text)
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
