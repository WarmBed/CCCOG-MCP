using System.Text.Json;

namespace CCCG.Router;

internal sealed class RouterEventLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly bool mirrorToStandardError;

    public RouterEventLog(string? path = null, bool mirrorToStandardError = true)
    {
        this.mirrorToStandardError = mirrorToStandardError;
        Path = path ?? DefaultPath();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        writer = new StreamWriter(
            new FileStream(
                Path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
        {
            AutoFlush = true
        };
    }

    public string Path { get; }

    public void Write(string eventName, object? data = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            @event = eventName,
            data
        });

        lock (gate)
        {
            writer.WriteLine(line);
            if (mirrorToStandardError)
            {
                Console.Error.WriteLine($"[CCCG] {eventName} {Summarize(data)}");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer.Dispose();
        }
    }

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = System.IO.Path.Combine(local, "CCCG", "router-data");
        var name = $"router-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssZ}-{Environment.ProcessId}.jsonl";
        return System.IO.Path.Combine(directory, name);
    }

    private static string Summarize(object? data) => data is null
        ? string.Empty
        : JsonSerializer.Serialize(data);
}
