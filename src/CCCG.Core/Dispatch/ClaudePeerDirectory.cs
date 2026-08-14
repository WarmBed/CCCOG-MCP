using System.Diagnostics;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class ClaudePeerDirectory
{
    private readonly string home;
    private readonly Func<int, bool> processIsAlive;

    public ClaudePeerDirectory(
        string? home = null,
        Func<int, bool>? processIsAlive = null)
    {
        this.home = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude")
            : home;
        this.processIsAlive = processIsAlive ?? IsProcessAlive;
    }

    public PeerList List(string? cwd = null, int limit = 20)
    {
        var active = ReadActiveSessions();
        var peers = ReadTranscripts(cwd)
            .Select(peer => MergeActive(peer, active))
            .ToList();

        // A just-started Desktop session can register before its transcript exists.
        foreach (var entry in active.Values)
        {
            if (peers.Any(peer => string.Equals(
                    peer.SessionId,
                    entry.SessionId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            peers.Add(ToPeer(entry));
        }

        var filtered = PeerListing.FilterAndRank(peers, cwd, limit);
        var total = string.IsNullOrWhiteSpace(cwd)
            ? peers.Count
            : peers.Count(peer => PeerListing.PathsMatch(peer.Cwd, cwd));
        return new PeerList(
            "claude",
            cwd,
            filtered,
            total,
            true,
            "Live Claude Desktop sessions own their writer and must receive messages through Desktop session management. Resumable sessions can be continued directly by CCCG.");
    }

    public Peer? Inspect(string sessionId) =>
        List(limit: 100)
            .Peers
            .FirstOrDefault(peer => string.Equals(
                peer.SessionId,
                sessionId,
                StringComparison.OrdinalIgnoreCase))
        ?? ReadTranscripts()
            .FirstOrDefault(peer => string.Equals(
                peer.SessionId,
                sessionId,
                StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Peer> ReadTranscripts(string? cwd = null)
    {
        var projectsRoot = Path.Combine(home, "projects");
        if (!Directory.Exists(projectsRoot))
        {
            yield break;
        }

        IEnumerable<string> projectDirectories;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var exact = Path.Combine(projectsRoot, EncodeProjectPath(cwd));
            projectDirectories = Directory.Exists(exact)
                ? new[] { exact }
                : Array.Empty<string>();
        }
        else
        {
            projectDirectories = Directory.EnumerateDirectories(projectsRoot);
        }

        foreach (var projectDirectory in projectDirectories)
        {
            IEnumerable<string> transcripts;
            try
            {
                transcripts = Directory.EnumerateFiles(
                    projectDirectory,
                    "*.jsonl",
                    SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var transcript in transcripts)
            {
                var peer = ReadTranscript(transcript);
                if (peer is null
                    || (!string.IsNullOrWhiteSpace(cwd)
                        && !PeerListing.PathsMatch(peer.Cwd, cwd)))
                {
                    continue;
                }

                yield return peer;
            }
        }
    }

    private static Peer? ReadTranscript(string path)
    {
        var file = new FileInfo(path);
        var sessionId = Path.GetFileNameWithoutExtension(path);
        if (!Guid.TryParse(sessionId, out _))
        {
            return null;
        }

        string? cwd = null;
        string? title = null;
        string? model = null;
        DateTimeOffset? openedAt = null;
        var messages = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    cwd ??= String(root, "cwd");
                    openedAt ??= Timestamp(root, "timestamp");
                    var type = String(root, "type");
                    if (type == "custom-title")
                    {
                        title = String(root, "customTitle") ?? title;
                    }
                    else if (type == "agent-name")
                    {
                        title ??= String(root, "agentName");
                    }

                    if (type is "user" or "assistant")
                    {
                        messages++;
                    }

                    if (type == "assistant"
                        && root.TryGetProperty("message", out var message))
                    {
                        model = String(message, "model") ?? model;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        return new Peer(
            "claude",
            sessionId,
            PeerStatus.Resumable,
            cwd,
            title,
            null,
            model,
            null,
            openedAt ?? file.CreationTimeUtc,
            file.LastWriteTimeUtc,
            messages);
    }

    private Dictionary<string, ActiveClaudeSession> ReadActiveSessions()
    {
        var result = new Dictionary<string, ActiveClaudeSession>(StringComparer.OrdinalIgnoreCase);
        var sessionsRoot = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var sessionId = String(root, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId)
                    || !root.TryGetProperty("pid", out var pidValue)
                    || !pidValue.TryGetInt32(out var pid)
                    || !processIsAlive(pid))
                {
                    continue;
                }

                var entry = new ActiveClaudeSession(
                    sessionId,
                    pid,
                    String(root, "cwd"),
                    String(root, "name"),
                    UnixMilliseconds(root, "startedAt"),
                    UnixMilliseconds(root, "updatedAt"));
                if (!result.TryGetValue(sessionId, out var existing)
                    || (entry.UpdatedAt ?? entry.StartedAt ?? DateTimeOffset.MinValue)
                        > (existing.UpdatedAt ?? existing.StartedAt ?? DateTimeOffset.MinValue))
                {
                    result[sessionId] = entry;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
            }
        }

        return result;
    }

    private static Peer MergeActive(
        Peer peer,
        IReadOnlyDictionary<string, ActiveClaudeSession> active)
    {
        if (!active.TryGetValue(peer.SessionId, out var entry))
        {
            return peer;
        }

        return peer with
        {
            Status = PeerStatus.LiveWorking,
            Cwd = entry.Cwd ?? peer.Cwd,
            Title = peer.Title ?? entry.Name,
            Pid = entry.Pid,
            OpenedAt = entry.StartedAt ?? peer.OpenedAt,
            UpdatedAt = entry.UpdatedAt ?? peer.UpdatedAt
        };
    }

    private static Peer ToPeer(ActiveClaudeSession entry) => new(
        "claude",
        entry.SessionId,
        PeerStatus.LiveWorking,
        entry.Cwd,
        entry.Name,
        null,
        null,
        entry.Pid,
        entry.StartedAt,
        entry.UpdatedAt,
        null);

    private static string EncodeProjectPath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(':', '-')
            .Replace('\\', '-')
            .Replace('/', '-');

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Timestamp(JsonElement element, string property) =>
        DateTimeOffset.TryParse(String(element, property), out var value) ? value : null;

    private static DateTimeOffset? UnixMilliseconds(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ActiveClaudeSession(
        string SessionId,
        int Pid,
        string? Cwd,
        string? Name,
        DateTimeOffset? StartedAt,
        DateTimeOffset? UpdatedAt);
}
