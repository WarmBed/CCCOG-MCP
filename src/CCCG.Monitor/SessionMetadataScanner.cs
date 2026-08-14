using CCCG.Core.Monitoring;

namespace CCCG.Monitor;

internal sealed record SessionMetadataChange(
    SessionMetadataSnapshot Snapshot,
    bool IsInitial);

internal sealed class SessionMetadataScanner
{
    private readonly IReadOnlyList<string> roots;
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> known =
        new(StringComparer.OrdinalIgnoreCase);

    public SessionMetadataScanner(IReadOnlyList<string> roots)
    {
        this.roots = roots;
    }

    public IReadOnlyList<SessionMetadataChange> Scan()
    {
        var changes = new List<SessionMetadataChange>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in paths)
            {
                var file = new FileInfo(path);
                var signature = (file.Length, file.LastWriteTimeUtc);
                if (known.TryGetValue(path, out var previous) && previous == signature)
                {
                    continue;
                }

                var initial = !known.ContainsKey(path);
                var snapshot = SessionMetadataReader.TryRead(path);
                if (snapshot is null)
                {
                    continue;
                }

                known[path] = signature;
                changes.Add(new SessionMetadataChange(snapshot, initial));
            }
        }

        return changes;
    }
}
