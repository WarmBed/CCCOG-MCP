using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed record CodexHome(string Path)
{
    public static CodexHome Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new CodexHome(configured);
        }

        return new CodexHome(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex"));
    }
}

public sealed class CodexPeerDirectory
{
    private readonly string codexHome;
    private readonly Func<string, bool> isLockHeld;
    private readonly Func<string, int?> lockHolderPid;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan workingWindow;

    public CodexPeerDirectory(
        CodexHome home,
        Func<string, bool>? isLockHeld = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? workingWindow = null,
        Func<string, int?>? lockHolderPid = null)
        : this(home.Path, isLockHeld, clock, workingWindow, lockHolderPid)
    {
    }

    public CodexPeerDirectory(
        string codexHome,
        Func<string, bool>? isLockHeld = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? workingWindow = null,
        Func<string, int?>? lockHolderPid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        this.codexHome = codexHome;
        this.isLockHeld = isLockHeld ?? LockIsHeld;
        this.lockHolderPid = lockHolderPid ?? ProcessLockHolder.TryGetPid;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.workingWindow = workingWindow ?? TimeSpan.FromSeconds(90);
    }

    public PeerList List(string? cwd = null, int limit = 20)
    {
        var all = LoadParents();
        var page = PeerListing.FilterAndRank(all, cwd, limit);
        return new PeerList(
            "codex",
            string.IsNullOrWhiteSpace(cwd) ? null : cwd,
            page,
            all.Count(peer => string.IsNullOrWhiteSpace(cwd) || PeerListing.PathsMatch(peer.Cwd, cwd)),
            CanCreateNew: true,
            PeerListing.Note("Codex"));
    }

    public Peer? Inspect(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return LoadParents().FirstOrDefault(peer =>
            string.Equals(peer.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private List<Peer> LoadParents()
    {
        var titles = ReadIndexTitles();
        var byId = new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase);
        var sessionsRoot = Path.Combine(codexHome, "sessions");
        if (Directory.Exists(sessionsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var parsed = ReadRolloutMeta(file);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.SessionId))
                {
                    continue;
                }

                // Subagent rollouts belong to the parent session_id. Never list
                // them as their own peer, but keep cwd/model for the parent.
                if (parsed.IsSubagent && !titles.ContainsKey(parsed.SessionId)
                    && !byId.ContainsKey(parsed.SessionId))
                {
                    continue;
                }

                var lockPath = LockPath(parsed.SessionId);
                var live = isLockHeld(lockPath);
                var peer = new Peer(
                    "codex",
                    parsed.SessionId,
                    PeerListing.Classify(parsed.UpdatedAt, live, clock(), workingWindow),
                    parsed.Cwd,
                    titles.GetValueOrDefault(parsed.SessionId),
                    LastTurnSummary: null,
                    parsed.Model,
                    Pid: live ? lockHolderPid(lockPath) : null,
                    parsed.OpenedAt,
                    parsed.UpdatedAt,
                    MessageCount: null);

                if (!byId.TryGetValue(parsed.SessionId, out var existing))
                {
                    byId[parsed.SessionId] = peer;
                    continue;
                }

                byId[parsed.SessionId] = existing with
                {
                    Status = PeerListing.Classify(
                        parsed.UpdatedAt ?? existing.UpdatedAt,
                        live || existing.Status.StartsWith("live-", StringComparison.Ordinal),
                        clock(),
                        workingWindow),
                    Cwd = existing.Cwd ?? parsed.Cwd,
                    Title = existing.Title ?? peer.Title,
                    Model = existing.Model ?? parsed.Model,
                    Pid = existing.Pid ?? peer.Pid,
                    OpenedAt = existing.OpenedAt ?? parsed.OpenedAt,
                    UpdatedAt = Max(existing.UpdatedAt, parsed.UpdatedAt)
                };
            }
        }

        foreach (var (id, title) in titles)
        {
            if (byId.ContainsKey(id))
            {
                continue;
            }

            var lockPath = LockPath(id);
            var live = isLockHeld(lockPath);
            byId[id] = new Peer(
                "codex",
                id,
                PeerListing.Classify(updated: null, live, clock(), workingWindow),
                Cwd: null,
                title,
                LastTurnSummary: null,
                Model: null,
                Pid: live ? lockHolderPid(lockPath) : null,
                OpenedAt: null,
                UpdatedAt: null,
                MessageCount: null);
        }

        return byId.Values.ToList();
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left >= right ? left : right;
    }

    private Dictionary<string, string> ReadIndexTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var indexPath = Path.Combine(codexHome, "session_index.jsonl");
        if (!File.Exists(indexPath))
        {
            return titles;
        }

        foreach (var line in File.ReadLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var title = doc.RootElement.TryGetProperty("thread_name", out var name)
                    ? name.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    titles[id] = title;
                }
            }
            catch (JsonException)
            {
            }
        }

        return titles;
    }

    private static CodexRolloutMeta? ReadRolloutMeta(string path)
    {
        string? line;
        try
        {
            using var reader = new StreamReader(path);
            line = reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("payload", out var payload))
            {
                return null;
            }

            var threadSource = payload.TryGetProperty("thread_source", out var source)
                ? source.GetString()
                : null;
            var sessionId = payload.TryGetProperty("session_id", out var session)
                ? session.GetString()
                : null;
            var timestamp = payload.TryGetProperty("timestamp", out var stamp)
                && stamp.TryGetDateTimeOffset(out var opened)
                    ? opened
                    : (DateTimeOffset?)null;
            DateTimeOffset? updated = File.GetLastWriteTimeUtc(path);

            return new CodexRolloutMeta(
                sessionId,
                payload.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null,
                payload.TryGetProperty("model_provider", out var model) ? model.GetString() : null,
                timestamp,
                updated,
                string.Equals(threadSource, "subagent", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string LockPath(string sessionId) =>
        Path.Combine(codexHome, "thread-writer-locks", sessionId + ".lock");

    private static bool LockIsHeld(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed record CodexRolloutMeta(
        string? SessionId,
        string? Cwd,
        string? Model,
        DateTimeOffset? OpenedAt,
        DateTimeOffset? UpdatedAt,
        bool IsSubagent);
}
