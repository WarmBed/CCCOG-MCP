using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCCG.Core.Dispatch;

var runtime = new WorkerRuntime();
if (args is ["run-job", var jobId])
{
    runtime.Runner.Run(jobId);
    return;
}

var input = (await Console.In.ReadLineAsync())?.TrimStart('\uFEFF');
if (string.IsNullOrWhiteSpace(input))
{
    return;
}

DispatchBackendResponse response;
try
{
    var request = JsonSerializer.Deserialize<DispatchBackendRequest>(input, WorkerRuntime.JsonOptions)
        ?? throw new InvalidDataException("Worker request is empty.");
    var payload = runtime.Invoke(request);
    response = new DispatchBackendResponse
    {
        Ok = true,
        Payload = payload,
        WorkerVersion = WorkerRuntime.WorkerVersion,
        WorkerPid = Environment.ProcessId
    };
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    response = new DispatchBackendResponse
    {
        Ok = false,
        Error = exception.Message,
        WorkerVersion = WorkerRuntime.WorkerVersion,
        WorkerPid = Environment.ProcessId
    };
}

Console.WriteLine(JsonSerializer.Serialize(response, WorkerRuntime.JsonOptions));

sealed class WorkerRuntime
{
    public static readonly string WorkerVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly GrokPeerDirectory grok = new(GrokHome.Resolve());
    private readonly CodexPeerDirectory codex = new(CodexHome.Resolve());
    private readonly ClaudePeerDirectory claude = new();
    private readonly DispatchBindingStore bindings = new();
    private readonly InboxLedger inbox = new();

    public WorkerRuntime()
    {
        var jobs = new DispatchJobStore();
        Runner = new DispatchRunner(
            jobs,
            provider => provider switch
            {
                "codex" => codex.List(limit: 100).Peers,
                "claude" => claude.List(limit: 100).Peers,
                _ => grok.List(limit: 100).Peers
            },
            bindings: bindings,
            inbox: inbox);
    }

    public DispatchRunner Runner { get; }

    public string Invoke(DispatchBackendRequest request)
    {
        return request.Operation switch
        {
            "list" => Serialize(List(
                String(request.Arguments, "provider") ?? "grok",
                String(request.Arguments, "cwd"),
                Integer(request.Arguments, "limit", 20))),
            "inspect" => Serialize(Inspect(
                Required(request.Arguments, "sessionId"),
                String(request.Arguments, "provider") ?? "grok")),
            "dispatch" => Dispatch(request.Arguments, wait: false),
            "dispatchWait" => Dispatch(request.Arguments, wait: true),
            "jobStatus" => Serialize(Runner.Status(Required(request.Arguments, "jobId"))),
            "jobCollect" => Serialize(Runner.Collect(Required(request.Arguments, "jobId"))),
            "inboxPost" => Serialize(inbox.Post(
                Required(request.Arguments, "fromRole"),
                Required(request.Arguments, "toRole"),
                Required(request.Arguments, "content"),
                String(request.Arguments, "fromProvider"),
                String(request.Arguments, "fromSessionId"),
                String(request.Arguments, "toProvider"),
                String(request.Arguments, "toSessionId"),
                String(request.Arguments, "jobId"))),
            "inboxList" => Serialize(new
            {
                messages = inbox.List(
                    Boolean(request.Arguments, "unreadOnly", false),
                    Integer(request.Arguments, "limit", 20))
            }),
            "inboxAck" => Serialize(inbox.Ack(Required(request.Arguments, "messageId"))
                ?? throw new InvalidOperationException(
                    $"Unknown inbox message '{Required(request.Arguments, "messageId")}'.")),
            "runtimeStatus" => Serialize(new
            {
                workerVersion = WorkerVersion,
                workerPid = Environment.ProcessId,
                executable = Environment.ProcessPath,
                hotReload = "next-call"
            }),
            _ => throw new ArgumentException($"Unknown worker operation '{request.Operation}'.")
        };
    }

    private string Dispatch(JsonElement arguments, bool wait)
    {
        var job = Runner.Enqueue(
            String(arguments, "provider") ?? "grok",
            Required(arguments, "prompt"),
            String(arguments, "sessionId"),
            String(arguments, "cwd"),
            Boolean(arguments, "allowNew", true));
        if (wait)
        {
            Runner.Run(job.JobId);
            return Serialize(Runner.Collect(job.JobId));
        }

        var workerPid = StartDetachedJob(job.JobId);
        Runner.MarkWorker(job.JobId, workerPid);
        return Serialize(job);
    }

    private static int StartDetachedJob(string jobId)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the dispatch worker executable.");
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("run-job");
        info.ArgumentList.Add(jobId);
        var process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start the detached dispatch job worker.");
        var pid = process.Id;
        process.Dispose();
        return pid;
    }

    private PeerList List(string provider, string? cwd, int limit)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider == "grok")
        {
            return Mark(grok.List(cwd, limit), "grok", cwd);
        }

        if (provider == "codex")
        {
            return Mark(codex.List(cwd, limit), "codex", cwd);
        }

        if (provider == "claude")
        {
            return Mark(claude.List(cwd, limit), "claude", cwd);
        }

        if (provider != "all")
        {
            throw new ArgumentException("Provider must be grok, codex, claude, or all.");
        }

        var grokList = Mark(grok.List(cwd, 100), "grok", cwd);
        var codexList = Mark(codex.List(cwd, 100), "codex", cwd);
        var claudeList = Mark(claude.List(cwd, 100), "claude", cwd);
        var merged = PeerListing.FilterAndRank(
            grokList.Peers.Concat(codexList.Peers).Concat(claudeList.Peers),
            cwd: null,
            limit);
        return new PeerList(
            "all",
            cwd,
            merged,
            grokList.TotalCount + codexList.TotalCount + claudeList.TotalCount,
            true,
            "Named live Grok/Codex peers are typed into the open window as a new turn. Live Claude Desktop still uses its native send_message tool.");
    }

    private object Inspect(string sessionId, string provider)
    {
        provider = provider.Trim().ToLowerInvariant();
        var peer = provider switch
        {
            "grok" => grok.Inspect(sessionId),
            "codex" => codex.Inspect(sessionId),
            "claude" => claude.Inspect(sessionId),
            "all" => grok.Inspect(sessionId) ?? codex.Inspect(sessionId) ?? claude.Inspect(sessionId),
            _ => throw new ArgumentException("Provider must be grok, codex, claude, or all.")
        };
        if (peer is null)
        {
            throw new InvalidOperationException($"No {provider} session '{sessionId}'.");
        }

        return peer with { Bound = bindings.IsManaged(peer.Provider, peer.SessionId) };
    }

    private PeerList Mark(PeerList list, string provider, string? cwd) =>
        list with { Peers = bindings.Mark(list.Peers, provider, cwd) };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string Required(JsonElement element, string property) =>
        String(element, property)
        ?? throw new ArgumentException($"{property} is required.");

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Integer(JsonElement element, string property, int fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static bool Boolean(JsonElement element, string property, bool fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
