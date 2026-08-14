using System.Diagnostics;
using System.Text;
using CCCG.Core;
using CCCG.Core.Providers;
using CCCG.Router;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var parsed = ClaudeArguments.Parse(args);
var configPath = parsed.ConfigPath;
if (string.IsNullOrWhiteSpace(configPath)
    && Environment.ProcessPath is { } processPath)
{
    var adjacentConfig = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(processPath)!,
        "cccg.routes.json");
    if (File.Exists(adjacentConfig))
    {
        configPath = adjacentConfig;
    }
}

if (string.IsNullOrWhiteSpace(configPath))
{
    Console.Error.WriteLine(
        "cccg-router requires --cccg-config <routes.json>, CCCG_CONFIG_PATH, "
        + "or an adjacent cccg.routes.json file.");
    return 2;
}

RouterConfiguration configuration;
try
{
    configuration = RouterConfiguration.Load(configPath);
}
catch (Exception exception) when (
    exception is IOException
    or UnauthorizedAccessException
    or InvalidDataException)
{
    Console.Error.WriteLine($"cccg-router configuration error: {exception.Message}");
    return 2;
}

var currentModel = parsed.Model;
if (string.IsNullOrWhiteSpace(currentModel))
{
    if (configuration.UsesPassthrough)
    {
        return await RunPassthroughAsync(
            configuration,
            parsed.ForwardedArguments,
            requestedModel: null);
    }

    Console.Error.WriteLine("cccg-router requires an initial --model alias.");
    return 2;
}

if (!configuration.TryResolve(currentModel, out _))
{
    if (!configuration.UsesPassthrough)
    {
        Console.Error.WriteLine(
            $"No CCCG route is configured for Claude model '{currentModel}'.");
        return 2;
    }

    return await RunPassthroughAsync(
        configuration,
        parsed.ForwardedArguments,
        currentModel);
}

using var diagnostics = new RouterEventLog();
var output = new ProtocolOutput();
var sessionId = "cccg_" + Guid.NewGuid().ToString("N");
var cwd = Environment.CurrentDirectory;
var permissionMode = parsed.PermissionMode ?? "default";
await output.WriteAsync(ClaudeProtocol.CreateInit(
    sessionId,
    cwd,
    currentModel,
    permissionMode));
diagnostics.Write("router.started", new
{
    pid = Environment.ProcessId,
    requestedModel = currentModel,
    config = System.IO.Path.GetFullPath(configPath),
    log = diagnostics.Path
});

Task? activeTurn = null;
CancellationTokenSource? activeTurnCancellation = null;

while (await Console.In.ReadLineAsync() is { } line)
{
    var frame = ClaudeProtocol.ParseInput(line);
    switch (frame.Kind)
    {
        case ClaudeFrameKind.SetModel:
            if (string.IsNullOrWhiteSpace(frame.Model)
                || !configuration.TryResolve(frame.Model, out _))
            {
                diagnostics.Write("route.rejected", new { requestedModel = frame.Model });
                if (!string.IsNullOrWhiteSpace(frame.RequestId))
                {
                    await output.WriteAsync(ClaudeProtocol.CreateControlSuccess(
                        frame.RequestId,
                        new { accepted = false, reason = "unmapped_model" }));
                }

                break;
            }

            currentModel = frame.Model;
            diagnostics.Write("route.selected", new { requestedModel = currentModel });
            if (!string.IsNullOrWhiteSpace(frame.RequestId))
            {
                await output.WriteAsync(ClaudeProtocol.CreateControlSuccess(
                    frame.RequestId,
                    new { accepted = true, model = currentModel }));
            }

            break;

        case ClaudeFrameKind.Interrupt:
            activeTurnCancellation?.Cancel();
            diagnostics.Write("turn.interrupt_requested", new { active = activeTurn is not null });
            if (!string.IsNullOrWhiteSpace(frame.RequestId))
            {
                await output.WriteAsync(ClaudeProtocol.CreateControlSuccess(
                    frame.RequestId,
                    new { interrupted = activeTurn is not null }));
            }

            break;

        case ClaudeFrameKind.User:
            if (activeTurn is { IsCompleted: false })
            {
                await output.WriteAsync(ClaudeProtocol.CreateErrorResult(
                    sessionId,
                    "CCCG rejected a concurrent user turn while another turn is active.",
                    0));
                diagnostics.Write("turn.rejected_busy");
                break;
            }

            activeTurnCancellation?.Dispose();
            activeTurnCancellation = new CancellationTokenSource();
            var turnModel = currentModel;
            var prompt = frame.Text!;
            activeTurn = RunTurnAsync(
                configuration,
                turnModel,
                prompt,
                sessionId,
                cwd,
                diagnostics,
                output,
                activeTurnCancellation.Token);
            break;

        case ClaudeFrameKind.Unknown:
            diagnostics.Write("input.ignored", new
            {
                reason = "unknown_frame",
                length = line.Length,
                prefixCodePoints = line.Take(4).Select(character => (int)character).ToArray()
            });
            break;
    }
}

if (activeTurn is not null)
{
    await activeTurn;
}

activeTurnCancellation?.Dispose();
diagnostics.Write("router.stopped");
return 0;

static async Task<int> RunPassthroughAsync(
    RouterConfiguration configuration,
    IReadOnlyList<string> forwardedArguments,
    string? requestedModel)
{
    using var diagnostics = new RouterEventLog(mirrorToStandardError: false);
    diagnostics.Write("passthrough.started", new
    {
        pid = Environment.ProcessId,
        requestedModel,
        executable = configuration.OriginalClaudePath,
        argumentCount = forwardedArguments.Count,
        log = diagnostics.Path
    });

    try
    {
        var exitCode = await ProcessPassthrough.RunAsync(
            configuration.OriginalClaudePath!,
            forwardedArguments,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.OpenStandardError());
        diagnostics.Write("passthrough.completed", new
        {
            requestedModel,
            exitCode
        });
        return exitCode;
    }
    catch (Exception exception) when (
        exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException)
    {
        diagnostics.Write("passthrough.failed", new
        {
            requestedModel,
            errorType = exception.GetType().Name,
            error = exception.Message
        });
        Console.Error.WriteLine($"CCCG passthrough error: {exception.Message}");
        return 126;
    }
}

static async Task RunTurnAsync(
    RouterConfiguration configuration,
    string requestedModel,
    string prompt,
    string sessionId,
    string cwd,
    RouterEventLog diagnostics,
    ProtocolOutput output,
    CancellationToken cancellationToken)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        var route = configuration.Resolve(requestedModel);
        diagnostics.Write("turn.started", new
        {
            requestedModel,
            provider = route.NormalizedProvider,
            backendModel = route.Model,
            promptLength = prompt.Length
        });

        CodexCompletion completion;
        switch (route.NormalizedProvider)
        {
            case "mock":
                completion = new CodexCompletion(
                    "CCCG-MOCK-OK",
                    route.Model,
                    route.Model,
                    false);
                break;

            case "codex-app-server":
                var client = new CodexAppServerClient(
                    diagnostic: message => diagnostics.Write(
                        "codex.stderr",
                        new { message }),
                    protocolDiagnostic: message => diagnostics.Write(
                        "codex.protocol",
                        new { message }));
                using (var backendTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
                {
                    backendTimeout.CancelAfter(TimeSpan.FromSeconds(120));
                    try
                    {
                        completion = await client.CompleteAsync(
                            prompt,
                            route.Model,
                            route.ReasoningEffort,
                            cwd,
                            backendTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "Codex backend did not complete within 120 seconds.");
                    }
                }
                break;

            case "xai-responses":
                throw new NotSupportedException(
                    "xAI live routing is not implemented or authorized in this milestone.");

            default:
                throw new InvalidDataException(
                    $"Unsupported provider '{route.Provider}'.");
        }

        stopwatch.Stop();
        var disclosed = ProviderDisclosure.Format(
            route.NormalizedProvider,
            completion.ActualModel,
            completion.Text,
            completion.RequestedModel);

        await output.WriteAsync(ClaudeProtocol.CreateAssistant(
            sessionId,
            requestedModel,
            disclosed));
        await output.WriteAsync(ClaudeProtocol.CreateSuccessResult(
            sessionId,
            disclosed,
            stopwatch.ElapsedMilliseconds));
        diagnostics.Write("turn.completed", new
        {
            requestedModel,
            provider = route.NormalizedProvider,
            requestedBackendModel = completion.RequestedModel,
            actualBackendModel = completion.ActualModel,
            completion.WasRerouted,
            completion.RerouteReason,
            durationMs = stopwatch.ElapsedMilliseconds
        });
    }
    catch (OperationCanceledException)
    {
        stopwatch.Stop();
        await output.WriteAsync(ClaudeProtocol.CreateErrorResult(
            sessionId,
            "CCCG turn interrupted by the client.",
            stopwatch.ElapsedMilliseconds));
        diagnostics.Write("turn.interrupted", new
        {
            requestedModel,
            durationMs = stopwatch.ElapsedMilliseconds
        });
    }
    catch (Exception exception)
    {
        stopwatch.Stop();
        await output.WriteAsync(ClaudeProtocol.CreateErrorResult(
            sessionId,
            $"CCCG backend error: {exception.Message}",
            stopwatch.ElapsedMilliseconds));
        diagnostics.Write("turn.failed", new
        {
            requestedModel,
            errorType = exception.GetType().Name,
            error = exception.Message,
            durationMs = stopwatch.ElapsedMilliseconds
        });
    }
}

internal sealed class ProtocolOutput
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task WriteAsync(string json)
    {
        await gate.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(json);
            await Console.Out.FlushAsync();
        }
        finally
        {
            gate.Release();
        }
    }
}
