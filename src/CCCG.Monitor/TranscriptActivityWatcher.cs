using System.Collections.Concurrent;
using CCCG.Core.Monitoring;

namespace CCCG.Monitor;

internal sealed record TranscriptActivity(
    string Path,
    long Length,
    long? Delta,
    IReadOnlyList<string> AppendedLines);

internal sealed class TranscriptActivityWatcher : IDisposable
{
    private readonly ConcurrentDictionary<string, byte> changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? watcher;
    private readonly Dictionary<string, long> knownLengths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IncrementalFileTailer> contentTailers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool readAppendedLines;

    public TranscriptActivityWatcher(string? root, bool readAppendedLines = false)
    {
        this.readAppendedLines = readAppendedLines;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        watcher = new FileSystemWatcher(root, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += (_, eventArgs) => changed[eventArgs.FullPath] = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                knownLengths[path] = new FileInfo(path).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        watcher.EnableRaisingEvents = true;
    }

    public bool IsActive => watcher is not null;

    public IReadOnlyList<TranscriptActivity> Drain()
    {
        var result = new List<TranscriptActivity>();
        foreach (var path in changed.Keys.ToArray())
        {
            if (!changed.TryRemove(path, out _))
            {
                continue;
            }

            try
            {
                var length = new FileInfo(path).Length;
                var hadPriorLength = knownLengths.TryGetValue(path, out var prior);
                long? delta = hadPriorLength ? length - prior : null;
                knownLengths[path] = length;
                var lines = Array.Empty<string>();
                if (readAppendedLines)
                {
                    if (!contentTailers.TryGetValue(path, out var tailer))
                    {
                        tailer = new IncrementalFileTailer(path, hadPriorLength ? prior : 0);
                        contentTailers[path] = tailer;
                    }

                    lines = tailer.ReadNewLines().ToArray();
                }

                result.Add(new TranscriptActivity(path, length, delta, lines));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    public void Dispose() => watcher?.Dispose();

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) =>
        changed[eventArgs.FullPath] = 0;
}
