namespace CCCG.Core.Dispatch;

public static class PeerListing
{
    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, 100);

    public static int StatusRank(Peer peer) => peer.Status switch
    {
        PeerStatus.LiveIdle => 0,
        PeerStatus.LiveWorking => 1,
        PeerStatus.Resumable => 2,
        _ => 3
    };

    public static bool PathsMatch(string? left, string right)
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

    public static string Classify(DateTimeOffset? updated, bool live, DateTimeOffset now, TimeSpan workingWindow)
    {
        return live ? PeerStatus.LiveWorking : PeerStatus.Resumable;
    }

    public static string Note(string provider) =>
        $"Named live {provider} peers are typed into the open window as a new turn. Auto-pick still prefers live-idle, then resumable, then create.";

    public static IReadOnlyList<Peer> FilterAndRank(IEnumerable<Peer> peers, string? cwd, int limit)
    {
        var filtered = string.IsNullOrWhiteSpace(cwd)
            ? peers
            : peers.Where(peer => PathsMatch(peer.Cwd, cwd));

        return filtered
            .OrderBy(StatusRank)
            .ThenByDescending(peer => peer.UpdatedAt ?? DateTimeOffset.MinValue)
            .Take(ClampLimit(limit))
            .ToList();
    }
}
