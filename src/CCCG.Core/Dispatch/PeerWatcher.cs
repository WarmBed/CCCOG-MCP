using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCCG.Core.Dispatch;

public sealed record PeerWatchRow(
    string SessionId,
    string Provider,
    bool Found,
    string? Status,
    int? Pid,
    string? Cwd,
    string? Title,
    string? Model);

public sealed record PeerWatchChange(
    string SessionId,
    string Field,
    object? From,
    object? To);

public sealed record PeerWatchResult(
    DateTimeOffset WatchedAt,
    IReadOnlyList<PeerWatchRow> Peers,
    IReadOnlyList<PeerWatchChange> Changes);

public sealed class PeerWatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] Providers = ["grok", "codex", "claude"];

    private readonly Func<string, string, Peer?> inspect;
    private readonly string root;
    private readonly Func<DateTimeOffset> clock;

    public PeerWatcher(
        Func<string, string, Peer?> inspect,
        string? root = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        this.inspect = inspect;
        this.root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch",
            "watch");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public PeerWatchResult Watch(string sessionIds, string provider = "all")
    {
        var ids = sessionIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException(
                "sessionIds must contain at least one comma-separated session id.",
                nameof(sessionIds));
        }

        provider = NormalizeProvider(provider);
        var peers = ids.Select(id => Resolve(id, provider)).ToList();
        var statePath = Path.Combine(root, "last.json");
        var stateKey = provider + "|" + string.Join("|", ids);
        List<PeerWatchChange> changes;

        using (CrossProcessFileGate.Acquire(statePath + ".lock", TimeSpan.FromSeconds(30)))
        {
            var state = ReadState(statePath);
            changes = state.Snapshots.TryGetValue(stateKey, out var previous)
                ? Diff(previous, peers)
                : [];
            state.Snapshots[stateKey] = peers;
            CrossProcessFileGate.AtomicWriteAllText(
                statePath,
                JsonSerializer.Serialize(state, JsonOptions));
        }

        return new PeerWatchResult(clock(), peers, changes);
    }

    private PeerWatchRow Resolve(string sessionId, string provider)
    {
        Peer? peer;
        if (provider == "all")
        {
            peer = Providers
                .Select(candidate => inspect(candidate, sessionId))
                .FirstOrDefault(candidate => candidate is not null);
        }
        else
        {
            peer = inspect(provider, sessionId);
        }

        return peer is null
            ? new PeerWatchRow(
                sessionId,
                provider,
                Found: false,
                Status: null,
                Pid: null,
                Cwd: null,
                Title: null,
                Model: null)
            : new PeerWatchRow(
                sessionId,
                peer.Provider,
                Found: true,
                peer.Status,
                peer.Pid,
                peer.Cwd,
                peer.Title,
                peer.Model);
    }

    private static List<PeerWatchChange> Diff(
        IReadOnlyList<PeerWatchRow> previous,
        IReadOnlyList<PeerWatchRow> current)
    {
        var before = previous.ToDictionary(row => row.SessionId, StringComparer.Ordinal);
        var changes = new List<PeerWatchChange>();
        foreach (var row in current)
        {
            if (!before.TryGetValue(row.SessionId, out var old))
            {
                continue;
            }

            if (old.Found != row.Found)
            {
                changes.Add(new PeerWatchChange(
                    row.SessionId,
                    "found",
                    old.Found,
                    row.Found));
                continue;
            }

            if (!row.Found)
            {
                continue;
            }

            if (!string.Equals(old.Status, row.Status, StringComparison.Ordinal))
            {
                changes.Add(new PeerWatchChange(
                    row.SessionId,
                    "status",
                    old.Status,
                    row.Status));
            }

            if (old.Pid != row.Pid)
            {
                changes.Add(new PeerWatchChange(
                    row.SessionId,
                    "pid",
                    old.Pid,
                    row.Pid));
            }
        }

        return changes;
    }

    private static PeerWatchState ReadState(string path)
    {
        if (!File.Exists(path))
        {
            return new PeerWatchState();
        }

        try
        {
            return JsonSerializer.Deserialize<PeerWatchState>(
                    File.ReadAllText(path),
                    JsonOptions)
                ?? new PeerWatchState();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The peer watch snapshot '{path}' is not valid JSON.",
                exception);
        }
    }

    private static string NormalizeProvider(string provider)
    {
        provider = provider.Trim().ToLowerInvariant();
        return provider is "grok" or "codex" or "claude" or "all"
            ? provider
            : throw new ArgumentException(
                "Provider must be grok, codex, claude, or all.",
                nameof(provider));
    }

    private sealed class PeerWatchState
    {
        public Dictionary<string, List<PeerWatchRow>> Snapshots { get; set; } = new();
    }
}
