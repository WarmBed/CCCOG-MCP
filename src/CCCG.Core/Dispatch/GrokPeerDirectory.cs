using System.Diagnostics;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class GrokPeerDirectory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string grokHome;
    private readonly Func<int, bool> isProcessAlive;

    public GrokPeerDirectory(
        GrokHome home,
        Func<int, bool>? isProcessAlive = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? workingWindow = null)
        : this(home.Path, isProcessAlive, clock, workingWindow)
    {
    }

    public GrokPeerDirectory(
        string grokHome,
        Func<int, bool>? isProcessAlive = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? workingWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grokHome);
        this.grokHome = grokHome;
        this.isProcessAlive = isProcessAlive ?? ProcessIsAlive;
        _ = clock;
        _ = workingWindow;
    }

    public PeerList List(string? cwd = null, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var peers = LoadAll();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            peers = peers
                .Where(peer => PathsMatch(peer.Cwd, cwd))
                .ToList();
        }

        var ordered = peers
            .OrderBy(StatusRank)
            .ThenByDescending(peer => peer.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        return new PeerList(
            "grok",
            string.IsNullOrWhiteSpace(cwd) ? null : cwd,
            ordered.Take(limit).ToList(),
            ordered.Count,
            CanCreateNew: true,
            "A live Grok PID owns its writer and is inspection-only. Resume a closed peer or create a CCCG-managed peer.");
    }

    public Peer? Inspect(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return LoadAll().FirstOrDefault(peer =>
            string.Equals(peer.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private List<Peer> LoadAll()
    {
        var byId = new Dictionary<string, Peer>(StringComparer.OrdinalIgnoreCase);

        foreach (var disk in ReadDiskSessions())
        {
            byId[disk.SessionId] = disk;
        }

        foreach (var live in ReadActiveSessions())
        {
            if (byId.TryGetValue(live.SessionId, out var existing))
            {
                byId[live.SessionId] = existing with
                {
                    Status = ClassifyLiveOrResumable(existing.UpdatedAt, pidAlive: true),
                    Pid = live.Pid,
                    OpenedAt = live.OpenedAt ?? existing.OpenedAt,
                    Cwd = existing.Cwd ?? live.Cwd
                };
            }
            else
            {
                byId[live.SessionId] = live with
                {
                    Status = ClassifyLiveOrResumable(live.UpdatedAt, pidAlive: true)
                };
            }
        }

        return byId.Values.ToList();
    }

    private IEnumerable<Peer> ReadActiveSessions()
    {
        var path = Path.Combine(grokHome, "active_sessions.json");
        if (!File.Exists(path))
        {
            yield break;
        }

        ActiveSessionFile[]? rows;
        try
        {
            rows = JsonSerializer.Deserialize<ActiveSessionFile[]>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (rows is null)
        {
            yield break;
        }

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SessionId) || row.Pid <= 0)
            {
                continue;
            }

            if (!isProcessAlive(row.Pid))
            {
                continue;
            }

            yield return new Peer(
                "grok",
                row.SessionId,
                PeerStatus.LiveWorking,
                row.Cwd,
                Title: null,
                LastTurnSummary: null,
                Model: null,
                row.Pid,
                row.OpenedAt,
                UpdatedAt: row.OpenedAt,
                MessageCount: null);
        }
    }

    private IEnumerable<Peer> ReadDiskSessions()
    {
        var root = Path.Combine(grokHome, "sessions");
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var group in Directory.EnumerateDirectories(root))
        {
            var groupCwd = ReadGroupCwd(group);
            foreach (var sessionDir in Directory.EnumerateDirectories(group))
            {
                var summaryPath = Path.Combine(sessionDir, "summary.json");
                if (!File.Exists(summaryPath))
                {
                    continue;
                }

                var peer = ReadSummary(summaryPath, Path.GetFileName(sessionDir), groupCwd);
                if (peer is not null)
                {
                    yield return peer;
                }
            }
        }
    }

    private Peer? ReadSummary(string summaryPath, string folderId, string? groupCwd)
    {
        GrokSummaryFile? summary;
        try
        {
            summary = JsonSerializer.Deserialize<GrokSummaryFile>(
                File.ReadAllText(summaryPath),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        var sessionId = summary?.Info?.Id;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = folderId;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var updated = summary?.LastActiveAt ?? summary?.UpdatedAt;
        return new Peer(
            "grok",
            sessionId,
            ClassifyLiveOrResumable(updated, pidAlive: false),
            summary?.Info?.Cwd ?? groupCwd,
            FirstNonEmpty(summary?.GeneratedTitle, summary?.SessionSummary),
            summary?.LastTurnSummary,
            summary?.CurrentModelId,
            Pid: null,
            summary?.CreatedAt,
            updated,
            summary?.NumMessages);
    }

    private string ClassifyLiveOrResumable(DateTimeOffset? updated, bool pidAlive)
    {
        return pidAlive ? PeerStatus.LiveWorking : PeerStatus.Resumable;
    }

    private static string? ReadGroupCwd(string groupDirectory)
    {
        var marker = Path.Combine(groupDirectory, ".cwd");
        if (File.Exists(marker))
        {
            var text = File.ReadAllText(marker).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return TryDecodeCwd(Path.GetFileName(groupDirectory));
    }

    internal static string? TryDecodeCwd(string encoded)
    {
        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool PathsMatch(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static int StatusRank(Peer peer) => peer.Status switch
    {
        PeerStatus.LiveIdle => 0,
        PeerStatus.LiveWorking => 1,
        PeerStatus.Resumable => 2,
        _ => 3
    };

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
