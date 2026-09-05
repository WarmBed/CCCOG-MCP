using System.Text.Json.Serialization;

namespace CCCG.Core.Dispatch;

public static class DispatchJobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class DispatchJob
{
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("requestedSessionId")]
    public string? RequestedSessionId { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("hopCount")]
    public int? HopCount { get; set; }

    [JsonPropertyName("hopSource")]
    public string? HopSource { get; set; }

    [JsonPropertyName("hopChain")]
    public string? HopChain { get; set; }

    [JsonPropertyName("callerLabel")]
    public string? CallerLabel { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = DispatchJobStatus.Queued;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("allowNew")]
    public bool AllowNew { get; set; }

    [JsonPropertyName("affinityKey")]
    public string? AffinityKey { get; set; }

    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("workerPid")]
    public int? WorkerPid { get; set; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finishedAt")]
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonPropertyName("promptChars")]
    public int PromptChars { get; set; }

    [JsonPropertyName("ownerPid")]
    public int? OwnerPid { get; set; }

    [JsonPropertyName("receiptStatus")]
    public string? ReceiptStatus { get; set; }

    [JsonPropertyName("deliveredAt")]
    public DateTimeOffset? DeliveredAt { get; set; }

    [JsonPropertyName("peerTurnsBefore")]
    public int? PeerTurnsBefore { get; set; }

    [JsonPropertyName("peerTurnsAfter")]
    public int? PeerTurnsAfter { get; set; }

    /// <summary>
    /// The exact argv (file name plus arguments) this job launched the
    /// provider with, recorded before launch so it survives even if Start
    /// throws. Exists because a prior investigation into "grok wakes then
    /// immediately sleeps" burned a full round trip with no way to confirm
    /// post-hoc whether a suspect flag change had actually reached the
    /// spawned process.
    /// </summary>
    [JsonPropertyName("providerArgv")]
    public string[]? ProviderArgv { get; set; }

    /// <summary>
    /// The provider's own turn-ending stopReason marker for this job, when
    /// the provider's stdout schema carries one (currently grok's "stopReason").
    /// Recorded even on a "succeeded" job so a cancelled-but-exit-0 turn
    /// (see ProviderOutputParser.FindError's grok branch) leaves a visible
    /// trail instead of looking identical to a real success.
    /// </summary>
    [JsonPropertyName("providerStopReason")]
    public string? ProviderStopReason { get; set; }

    /// <summary>
    /// How many automatic re-attempts DispatchRunner made after grok's own
    /// permission engine cancelled a turn (see ProviderCommand's
    /// GrokCancelRetriesEnvVariable). Absent/null means no retry was needed
    /// -- the first attempt either succeeded or failed for a non-retryable
    /// reason. A job can succeed with RetryCount > 0.
    /// </summary>
    [JsonPropertyName("retryCount")]
    public int? RetryCount { get; set; }

    /// <summary>
    /// Informational version of the dispatch worker binary that actually
    /// ran this job (cccg-dispatch-worker's AssemblyInformationalVersion),
    /// stamped when Run() takes the job. Exists because "which worker build
    /// ran it?" was unanswerable post-hoc: worker-current.json only says
    /// what is installed NOW, not what a given past job executed under, and
    /// a version-timing question once cost hours of reflection-probing.
    /// </summary>
    [JsonPropertyName("workerVersion")]
    public string? WorkerVersion { get; set; }

    /// <summary>
    /// Provider-reported USD cost summed across every attempt of this job
    /// (grok's "total_cost_usd"; codex exec --json exposes no cost in this
    /// codebase's evidence, so it stays null there). Accumulated, not
    /// last-attempt-only, so an auto-retried job shows what it really
    /// billed rather than only the successful attempt's slice.
    /// </summary>
    [JsonPropertyName("providerCostUsd")]
    public double? ProviderCostUsd { get; set; }
}

public sealed record LaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);
