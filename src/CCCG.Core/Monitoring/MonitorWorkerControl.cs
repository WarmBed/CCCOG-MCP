using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCCG.Core.Monitoring;

public sealed record MonitorWorkerReady
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generation")]
    public required string Generation { get; init; }

    [JsonPropertyName("workerPid")]
    public int WorkerPid { get; init; }

    [JsonPropertyName("readyAtUtc")]
    public DateTimeOffset ReadyAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("runDirectory")]
    public required string RunDirectory { get; init; }

    [JsonPropertyName("contentCapture")]
    public bool ContentCapture { get; init; }

    [JsonPropertyName("monitorVersion")]
    public string? MonitorVersion { get; init; }
}

public static class MonitorWorkerControl
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void WriteReady(string path, MonitorWorkerReady ready) =>
        WriteJsonAtomic(path, ready);

    public static MonitorWorkerReady? TryReadReady(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MonitorWorkerReady>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void RequestStop(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.WriteLine(DateTimeOffset.UtcNow.ToString("O"));
    }

    public static bool IsStopRequested(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public static void WriteJsonAtomic<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(value, JsonOptions),
                new System.Text.UTF8Encoding(false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
