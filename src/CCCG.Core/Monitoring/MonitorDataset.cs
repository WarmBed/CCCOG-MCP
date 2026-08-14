using System.Text.Json;

namespace CCCG.Core.Monitoring;

public sealed class MonitorDataset : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly StreamWriter writer;
    private readonly StreamWriter? contentWriter;
    private readonly Dictionary<string, long> eventCounts = new(StringComparer.Ordinal);
    private bool disposed;

    private MonitorDataset(string runDirectory, StreamWriter writer, StreamWriter? contentWriter)
    {
        RunDirectory = runDirectory;
        this.writer = writer;
        this.contentWriter = contentWriter;
    }

    public string RunDirectory { get; }

    public long EventCount { get; private set; }

    public long ContentCount { get; private set; }

    public static MonitorDataset Create(
        string dataDirectory,
        IReadOnlyList<string> sources,
        string mode,
        bool captureContent = false)
    {
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'")
            + "_" + Guid.NewGuid().ToString("N")[..8];
        var runDirectory = Path.Combine(dataDirectory, "runs", runId);
        Directory.CreateDirectory(runDirectory);

        var manifest = new
        {
            schemaVersion = 1,
            monitorVersion = typeof(MonitorDataset).Assembly.GetName().Version?.ToString(),
            startedAtUtc = DateTimeOffset.UtcNow,
            mode,
            sources = sources.Select(MonitorPathRedactor.Redact).ToArray(),
            privacy = new
            {
                promptBodiesStored = captureContent,
                responseBodiesStored = captureContent,
                rawLogLinesStored = false,
                credentialsStored = false,
                thinkingStored = false,
                toolInputsStored = false,
                toolResultsStored = false,
                sessionIds = "HMAC-SHA256 pseudonyms"
            }
        };
        File.WriteAllText(
            Path.Combine(runDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var stream = new FileStream(
            Path.Combine(runDirectory, "events.jsonl"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        StreamWriter? contentWriter = null;
        if (captureContent)
        {
            var contentStream = new FileStream(
                Path.Combine(runDirectory, "content.jsonl"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            contentWriter = new StreamWriter(contentStream, new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        return new MonitorDataset(
            runDirectory,
            new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true },
            contentWriter);
    }

    public void Write(MonitorEvent monitorEvent)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        writer.WriteLine(JsonSerializer.Serialize(monitorEvent, JsonOptions));
        EventCount++;
        eventCounts[monitorEvent.Event] = eventCounts.GetValueOrDefault(monitorEvent.Event) + 1;
    }

    public void WriteContent(MonitorContentRecord record)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (contentWriter is null)
        {
            throw new InvalidOperationException("Content capture is not enabled for this dataset.");
        }

        contentWriter.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
        ContentCount++;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writer.Dispose();
        contentWriter?.Dispose();
        File.WriteAllText(
            Path.Combine(RunDirectory, "summary.json"),
            JsonSerializer.Serialize(
                new
                {
                    finishedAtUtc = DateTimeOffset.UtcNow,
                    eventCount = EventCount,
                    contentCount = ContentCount,
                    eventCounts = eventCounts.OrderByDescending(pair => pair.Value)
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
