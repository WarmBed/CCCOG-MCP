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
}

public sealed record LaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
