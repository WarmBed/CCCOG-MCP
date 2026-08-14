namespace CCCG.Core.Monitoring;

public sealed record MonitorSources(
    string? MainLog,
    IReadOnlyList<string> SessionRoots,
    string? TranscriptRoot);

public static class SourceDiscovery
{
    public static MonitorSources Discover()
    {
        var logs = new List<string>();
        var sessionRoots = new List<string>();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AddClaudeRoot(Path.Combine(roaming, "Claude"), logs, sessionRoots);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                AddClaudeRoot(
                    Path.Combine(package, "LocalCache", "Roaming", "Claude"),
                    logs,
                    sessionRoots);
            }
        }

        var mainLog = logs
            .Where(File.Exists)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
        var transcriptRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");

        return new MonitorSources(
            mainLog,
            sessionRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Directory.Exists(transcriptRoot) ? transcriptRoot : null);
    }

    private static void AddClaudeRoot(
        string root,
        ICollection<string> logs,
        ICollection<string> sessionRoots)
    {
        logs.Add(Path.Combine(root, "logs", "main.log"));
        sessionRoots.Add(Path.Combine(root, "claude-code-sessions"));
    }
}
