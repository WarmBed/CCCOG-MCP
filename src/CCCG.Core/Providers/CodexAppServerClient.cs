using System.Text.Json;

namespace CCCG.Core.Providers;

public sealed class CodexAppServerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly Func<CancellationToken, Task<IJsonLineTransport>> transportFactory;
    private readonly Action<string>? protocolDiagnostic;

    public CodexAppServerClient(
        Func<CancellationToken, Task<IJsonLineTransport>>? transportFactory = null,
        Action<string>? diagnostic = null,
        Action<string>? protocolDiagnostic = null)
    {
        this.transportFactory = transportFactory
            ?? (token => ProcessJsonLineTransport.StartCodexAsync(diagnostic, token));
        this.protocolDiagnostic = protocolDiagnostic;
    }

    public async Task<CodexCompletion> CompleteAsync(
        string prompt,
        string model,
        string? reasoningEffort,
        string cwd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        await using var transport = await transportFactory(cancellationToken)
            .ConfigureAwait(false);

        await transport.WriteLineAsync(Serialize(new
        {
            method = "initialize",
            id = 0,
            @params = new
            {
                clientInfo = new
                {
                    name = "cccg_router",
                    title = "CCCG Router",
                    version = "0.1.0"
                }
            }
        }), cancellationToken).ConfigureAwait(false);

        await ReadResponseAsync(transport, 0, cancellationToken, diagnostic: protocolDiagnostic)
            .ConfigureAwait(false);

        await transport.WriteLineAsync(Serialize(new
        {
            method = "initialized",
            @params = new { }
        }), cancellationToken).ConfigureAwait(false);

        await transport.WriteLineAsync(Serialize(new
        {
            method = "thread/start",
            id = 1,
            @params = new
            {
                model,
                cwd = Path.GetFullPath(cwd),
                approvalPolicy = "never",
                sandbox = "read-only",
                ephemeral = true,
                serviceName = "cccg_router"
            }
        }), cancellationToken).ConfigureAwait(false);

        var threadResponse = await ReadResponseAsync(
                transport,
                1,
                cancellationToken,
                diagnostic: protocolDiagnostic)
            .ConfigureAwait(false);
        var threadResult = threadResponse.GetProperty("result");
        var threadId = RequiredString(
            threadResult.GetProperty("thread"),
            "id",
            "thread/start did not return a thread id.");
        var actualModel = OptionalString(threadResult, "model") ?? model;

        var turnParams = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["input"] = new[] { new { type = "text", text = prompt } },
            ["model"] = model
        };
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            turnParams["effort"] = reasoningEffort;
        }

        await transport.WriteLineAsync(Serialize(new
        {
            method = "turn/start",
            id = 2,
            @params = turnParams
        }), cancellationToken).ConfigureAwait(false);

        var pending = new Queue<string>();
        var turnResponse = await ReadResponseAsync(
                transport,
                2,
                cancellationToken,
                pending,
                protocolDiagnostic)
            .ConfigureAwait(false);
        var turnId = RequiredString(
            turnResponse.GetProperty("result").GetProperty("turn"),
            "id",
            "turn/start did not return a turn id.");

        var messages = new List<string>();
        var wasRerouted = false;
        string? rerouteReason = null;

        while (true)
        {
            var line = pending.Count > 0
                ? pending.Dequeue()
                : await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException(
                        "Codex app-server closed before turn/completed.");

            using var document = ParseMessage(line);
            var root = document.RootElement;
            protocolDiagnostic?.Invoke(DescribeMessage(root));

            RejectUnexpectedServerRequest(root);
            if (!root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var method = methodElement.GetString();
            if (string.Equals(method, "model/rerouted", StringComparison.Ordinal))
            {
                var parameters = root.GetProperty("params");
                if (MatchesTurn(parameters, threadId, turnId))
                {
                    actualModel = OptionalString(parameters, "toModel") ?? actualModel;
                    rerouteReason = parameters.TryGetProperty("reason", out var reason)
                        ? reason.ToString()
                        : null;
                    wasRerouted = !string.Equals(model, actualModel, StringComparison.Ordinal);
                }

                continue;
            }

            if (string.Equals(method, "item/completed", StringComparison.Ordinal))
            {
                var parameters = root.GetProperty("params");
                if (MatchesTurn(parameters, threadId, turnId)
                    && parameters.TryGetProperty("item", out var item)
                    && string.Equals(
                        OptionalString(item, "type"),
                        "agentMessage",
                        StringComparison.Ordinal)
                    && OptionalString(item, "text") is { Length: > 0 } text)
                {
                    messages.Add(text);
                }

                continue;
            }

            if (!string.Equals(method, "turn/completed", StringComparison.Ordinal))
            {
                continue;
            }

            var completedParams = root.GetProperty("params");
            if (!string.Equals(
                OptionalString(completedParams, "threadId"),
                threadId,
                StringComparison.Ordinal))
            {
                continue;
            }

            var turn = completedParams.GetProperty("turn");
            if (!string.Equals(OptionalString(turn, "id"), turnId, StringComparison.Ordinal))
            {
                continue;
            }

            var status = OptionalString(turn, "status");
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                var error = turn.TryGetProperty("error", out var errorElement)
                    ? OptionalString(errorElement, "message") ?? errorElement.ToString()
                    : null;
                throw new InvalidOperationException(
                    $"Codex turn ended with status '{status}': {error ?? "no error detail"}");
            }

            if (messages.Count == 0)
            {
                throw new InvalidDataException(
                    "Codex turn completed without an agentMessage item.");
            }

            return new CodexCompletion(
                string.Join("\n", messages),
                model,
                actualModel,
                wasRerouted,
                rerouteReason);
        }
    }

    private static async Task<JsonElement> ReadResponseAsync(
        IJsonLineTransport transport,
        int expectedId,
        CancellationToken cancellationToken,
        Queue<string>? pending = null,
        Action<string>? diagnostic = null)
    {
        while (true)
        {
            var line = await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException(
                    $"Codex app-server closed before response {expectedId}.");
            using var document = ParseMessage(line);
            var root = document.RootElement;
            diagnostic?.Invoke(DescribeMessage(root));

            RejectUnexpectedServerRequest(root);
            if (root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out var numericId)
                && numericId == expectedId)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = OptionalString(error, "message") ?? error.ToString();
                    throw new InvalidOperationException(
                        $"Codex app-server request {expectedId} failed: {message}");
                }

                return root.Clone();
            }

            if (root.TryGetProperty("method", out _))
            {
                pending?.Enqueue(line);
            }
        }
    }

    private static void RejectUnexpectedServerRequest(JsonElement root)
    {
        if (root.TryGetProperty("method", out var method)
            && root.TryGetProperty("id", out _))
        {
            throw new InvalidOperationException(
                $"Unexpected Codex app-server request '{method.GetString()}'. "
                + "CCCG runs with approval=never and fails closed instead of auto-approving.");
        }
    }

    private static bool MatchesTurn(
        JsonElement parameters,
        string threadId,
        string turnId) =>
        string.Equals(
            OptionalString(parameters, "threadId"),
            threadId,
            StringComparison.Ordinal)
        && string.Equals(
            OptionalString(parameters, "turnId"),
            turnId,
            StringComparison.Ordinal);

    private static string RequiredString(
        JsonElement element,
        string property,
        string error) => OptionalString(element, property)
            ?? throw new InvalidDataException(error);

    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonDocument ParseMessage(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Codex app-server emitted a non-JSON stdout line.",
                exception);
        }
    }

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static string DescribeMessage(JsonElement root)
    {
        if (root.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String)
        {
            return $"notification method={method.GetString()}";
        }

        if (root.TryGetProperty("id", out var id))
        {
            return root.TryGetProperty("error", out _)
                ? $"response id={id} error=true"
                : $"response id={id} error=false";
        }

        return "message kind=unknown";
    }
}
