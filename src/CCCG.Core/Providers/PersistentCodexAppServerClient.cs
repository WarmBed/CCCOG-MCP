using System.Text.Json;

namespace CCCG.Core.Providers;

public sealed class PersistentCodexAppServerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string sessionKey;
    private readonly CodexThreadBindingStore bindingStore;
    private readonly Func<CancellationToken, Task<IJsonLineTransport>> transportFactory;
    private readonly Action<string>? protocolDiagnostic;
    private readonly SemaphoreSlim turnGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Queue<string> pending = new();
    private IJsonLineTransport? transport;
    private FileStream? lease;
    private string? threadId;
    private string? activeTurnId;
    private int requestId;
    private bool disposed;

    public PersistentCodexAppServerClient(
        string sessionKey,
        CodexThreadBindingStore? bindingStore = null,
        Func<CancellationToken, Task<IJsonLineTransport>>? transportFactory = null,
        Action<string>? diagnostic = null,
        Action<string>? protocolDiagnostic = null,
        IReadOnlyDictionary<string, string>? processEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        this.sessionKey = sessionKey;
        this.bindingStore = bindingStore ?? new CodexThreadBindingStore();
        this.transportFactory = transportFactory
            ?? (token => ProcessJsonLineTransport.StartCodexAsync(
                diagnostic,
                token,
                processEnvironment));
        this.protocolDiagnostic = protocolDiagnostic;
    }

    public string? ThreadId => threadId;

    public async Task<CodexCompletion> CompleteAsync(
        string prompt,
        string model,
        string? reasoningEffort,
        string cwd,
        object? outputSchema = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        await turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
            await EnsureSessionAsync(model, cwd, cancellationToken).ConfigureAwait(false);
            var currentThreadId = threadId!;
            var turnParameters = new Dictionary<string, object?>
            {
                ["threadId"] = currentThreadId,
                ["input"] = new[] { new { type = "text", text = prompt } },
                ["model"] = model
            };
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                turnParameters["effort"] = reasoningEffort;
            }

            if (outputSchema is not null)
            {
                turnParameters["outputSchema"] = outputSchema;
            }

            var startId = NextRequestId();
            await SendAsync(new
            {
                method = "turn/start",
                id = startId,
                @params = turnParameters
            }, cancellationToken).ConfigureAwait(false);

            var startResponse = await ReadResponseAsync(startId, cancellationToken)
                .ConfigureAwait(false);
            var turnId = RequiredString(
                startResponse.GetProperty("result").GetProperty("turn"),
                "id",
                "turn/start did not return a turn id.");
            activeTurnId = turnId;

            var messages = new List<string>();
            var actualModel = model;
            var wasRerouted = false;
            string? rerouteReason = null;

            while (true)
            {
                var line = pending.Count > 0
                    ? pending.Dequeue()
                    : await transport!.ReadLineAsync(cancellationToken).ConfigureAwait(false)
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
                    if (MatchesTurn(parameters, currentThreadId, turnId))
                    {
                        actualModel = OptionalString(parameters, "toModel") ?? actualModel;
                        rerouteReason = parameters.TryGetProperty("reason", out var reason)
                            ? reason.ToString()
                            : null;
                        wasRerouted = !string.Equals(
                            model,
                            actualModel,
                            StringComparison.Ordinal);
                    }

                    continue;
                }

                if (string.Equals(method, "item/completed", StringComparison.Ordinal))
                {
                    var parameters = root.GetProperty("params");
                    if (MatchesTurn(parameters, currentThreadId, turnId)
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

                var completed = root.GetProperty("params");
                if (!string.Equals(
                        OptionalString(completed, "threadId"),
                        currentThreadId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var turn = completed.GetProperty("turn");
                if (!string.Equals(OptionalString(turn, "id"), turnId, StringComparison.Ordinal))
                {
                    continue;
                }

                var status = OptionalString(turn, "status");
                if (!string.Equals(status, "completed", StringComparison.Ordinal))
                {
                    throw new OperationCanceledException(
                        $"Codex turn ended with status '{status}'.");
                }

                if (messages.Count == 0)
                {
                    throw new InvalidDataException(
                        "Codex turn completed without an agentMessage item.");
                }

                SaveBinding(model);
                return new CodexCompletion(
                    string.Join("\n", messages),
                    model,
                    actualModel,
                    wasRerouted,
                    rerouteReason);
            }
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException)
            {
                // Transport-level death (app-server exited, pipe broke): the
                // dead transport must not be kept — every later turn would
                // fail against it forever. Tear it down so the next
                // CompleteAsync spawns a fresh app-server and resumes the
                // bound thread.
                await TeardownTransportAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            activeTurnId = null;
            turnGate.Release();
        }
    }

    /// <summary>
    /// Discards a broken transport (and its lease, pending queue, and thread
    /// identity) so the next turn rebuilds from the persisted thread binding.
    /// Runs under <see cref="turnGate"/>.
    /// </summary>
    private async Task TeardownTransportAsync()
    {
        pending.Clear();
        threadId = null;
        activeTurnId = null;
        var broken = transport;
        transport = null;
        if (broken is not null)
        {
            try
            {
                await broken.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Disposing an already-dead process can fail; the rebuild is
                // what matters.
            }
        }

        lease?.Dispose();
        lease = null;
    }

    public async Task<bool> InterruptAsync(CancellationToken cancellationToken = default)
    {
        var currentThreadId = threadId;
        var currentTurnId = activeTurnId;
        if (transport is null
            || string.IsNullOrWhiteSpace(currentThreadId)
            || string.IsNullOrWhiteSpace(currentTurnId))
        {
            return false;
        }

        await SendAsync(new
        {
            method = "turn/interrupt",
            id = NextRequestId(),
            @params = new { threadId = currentThreadId, turnId = currentTurnId }
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task EnsureSessionAsync(
        string model,
        string cwd,
        CancellationToken cancellationToken)
    {
        if (transport is not null)
        {
            return;
        }

        lease = bindingStore.AcquireLease(sessionKey);
        transport = await transportFactory(cancellationToken).ConfigureAwait(false);

        var initializeId = NextRequestId();
        await SendAsync(new
        {
            method = "initialize",
            id = initializeId,
            @params = new
            {
                clientInfo = new
                {
                    name = "cccg_model_transport",
                    title = "CCCG Model Transport",
                    version = "0.2.0"
                },
                capabilities = new { experimentalApi = true }
            }
        }, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(initializeId, cancellationToken).ConfigureAwait(false);
        await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken)
            .ConfigureAwait(false);

        var binding = bindingStore.Load(sessionKey);
        var threadRequestId = NextRequestId();
        if (binding is null)
        {
            await SendAsync(new
            {
                method = "thread/start",
                id = threadRequestId,
                @params = new
                {
                    model,
                    cwd = Path.GetFullPath(cwd),
                    approvalPolicy = "never",
                    sandbox = "read-only",
                    ephemeral = false,
                    serviceName = "cccg_model_transport"
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SendAsync(new
            {
                method = "thread/resume",
                id = threadRequestId,
                @params = new
                {
                    threadId = binding.ThreadId,
                    model,
                    cwd = Path.GetFullPath(cwd),
                    approvalPolicy = "never",
                    sandbox = "read-only"
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        var response = await ReadResponseAsync(threadRequestId, cancellationToken)
            .ConfigureAwait(false);
        threadId = RequiredString(
            response.GetProperty("result").GetProperty("thread"),
            "id",
            "Codex thread request did not return a thread id.");

        if (binding is null)
        {
            var now = DateTimeOffset.UtcNow;
            bindingStore.Save(sessionKey, new CodexThreadBinding(
                1,
                threadId,
                model,
                now,
                now));
        }
    }

    private void SaveBinding(string model)
    {
        var existing = bindingStore.Load(sessionKey);
        var now = DateTimeOffset.UtcNow;
        bindingStore.Save(sessionKey, new CodexThreadBinding(
            1,
            threadId!,
            model,
            existing?.CreatedAtUtc ?? now,
            now));
    }

    private async Task<JsonElement> ReadResponseAsync(
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = pending.Count > 0
                ? pending.Dequeue()
                : await transport!.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException(
                        $"Codex app-server closed before response {expectedId}.");
            using var document = ParseMessage(line);
            var root = document.RootElement;
            protocolDiagnostic?.Invoke(DescribeMessage(root));
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
                pending.Enqueue(line);
            }
        }
    }

    private async Task SendAsync(object value, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transport!.WriteLineAsync(
                JsonSerializer.Serialize(value, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private int NextRequestId() => Interlocked.Increment(ref requestId);

    private static void RejectUnexpectedServerRequest(JsonElement root)
    {
        if (root.TryGetProperty("method", out var method)
            && root.TryGetProperty("id", out _))
        {
            throw new InvalidOperationException(
                $"Unexpected Codex app-server request '{method.GetString()}'. "
                + "CCCG fails closed instead of auto-approving or invoking an undeclared tool.");
        }
    }

    private static bool MatchesTurn(
        JsonElement parameters,
        string threadId,
        string turnId) =>
        string.Equals(OptionalString(parameters, "threadId"), threadId, StringComparison.Ordinal)
        && string.Equals(OptionalString(parameters, "turnId"), turnId, StringComparison.Ordinal);

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

    private static string DescribeMessage(JsonElement root)
    {
        if (root.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String)
        {
            return root.TryGetProperty("id", out _)
                ? $"request method={method.GetString()}"
                : $"notification method={method.GetString()}";
        }

        if (root.TryGetProperty("id", out var id))
        {
            return root.TryGetProperty("error", out _)
                ? $"response id={id} error=true"
                : $"response id={id} error=false";
        }

        return "message kind=unknown";
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        lease?.Dispose();
        turnGate.Dispose();
        writeGate.Dispose();
    }
}
