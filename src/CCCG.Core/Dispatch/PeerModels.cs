using System.Text.Json.Serialization;

namespace CCCG.Core.Dispatch;

public static class PeerStatus
{
    public const string LiveIdle = "live-idle";
    public const string LiveWorking = "live-working";
    public const string Resumable = "resumable";
}

public sealed record Peer(
    string Provider,
    string SessionId,
    string Status,
    string? Cwd,
    string? Title,
    string? LastTurnSummary,
    string? Model,
    int? Pid,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? UpdatedAt,
    int? MessageCount,
    bool Bound = false);

public sealed record PeerList(
    string Provider,
    string? Workspace,
    IReadOnlyList<Peer> Peers,
    int TotalCount,
    bool CanCreateNew,
    string Note);

public sealed record GrokHome(string Path)
{
    public static GrokHome Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new GrokHome(configured);
        }

        return new GrokHome(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok"));
    }
}

internal sealed class ActiveSessionFile
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("opened_at")]
    public DateTimeOffset? OpenedAt { get; set; }
}

internal sealed class GrokSummaryFile
{
    [JsonPropertyName("info")]
    public GrokSummaryInfo? Info { get; set; }

    [JsonPropertyName("session_summary")]
    public string? SessionSummary { get; set; }

    [JsonPropertyName("generated_title")]
    public string? GeneratedTitle { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("last_active_at")]
    public DateTimeOffset? LastActiveAt { get; set; }

    [JsonPropertyName("num_messages")]
    public int? NumMessages { get; set; }

    [JsonPropertyName("current_model_id")]
    public string? CurrentModelId { get; set; }

    [JsonPropertyName("last_turn_summary")]
    public string? LastTurnSummary { get; set; }
}

internal sealed class GrokSummaryInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }
}
