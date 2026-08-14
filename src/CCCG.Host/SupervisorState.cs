using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CCCG.Core.Monitoring;

namespace CCCG.Host;

internal sealed record DesiredWorker
{
    public int SchemaVersion { get; init; } = 1;

    public required string RequestId { get; init; }

    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string Sha256 { get; init; }

    public required string StagedPath { get; init; }
}

internal sealed record RetiringWorker
{
    public required string Generation { get; init; }

    public int WorkerPid { get; init; }

    public required string WorkerPath { get; init; }

    public required string ReadyFile { get; init; }

    public required string StopFile { get; init; }
}

internal sealed record ActiveWorker
{
    public int SchemaVersion { get; init; } = 1;

    public required string RequestId { get; init; }

    public required string Generation { get; init; }

    public required string Sha256 { get; init; }

    public required string ConfigurationKey { get; init; }

    public required string WorkerPath { get; init; }

    public int WorkerPid { get; init; }

    public required string ReadyFile { get; init; }

    public required string StopFile { get; init; }

    public required string RunDirectory { get; init; }

    public bool ContentCapture { get; init; }

    public DateTimeOffset ActivatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ReadyAtUtc { get; init; }

    public IReadOnlyList<RetiringWorker> Retiring { get; init; } = Array.Empty<RetiringWorker>();
}

internal sealed record HostHeartbeat
{
    public int SchemaVersion { get; init; } = 1;

    public int HostPid { get; init; }

    public required string HostPath { get; init; }

    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? ActiveGeneration { get; init; }

    public int? ActiveWorkerPid { get; init; }
}

internal sealed record SupervisorFailure
{
    public int SchemaVersion { get; init; } = 1;

    public required string RequestId { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset FailedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string Error { get; init; }
}

internal sealed record HandoffRecord
{
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string NewGeneration { get; init; }

    public int NewWorkerPid { get; init; }

    public required string NewSha256 { get; init; }

    public string? PreviousGeneration { get; init; }

    public int? PreviousWorkerPid { get; init; }

    public bool PreviousStoppedCooperatively { get; init; }

    public required string NewRunDirectory { get; init; }

    public DateTimeOffset CandidateReadyAtUtc { get; init; }

    public double OverlapMilliseconds { get; init; }
}

internal sealed class SupervisorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SupervisorStore(string stateDirectory)
    {
        StateDirectory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(GenerationsDirectory);
    }

    public string StateDirectory { get; }

    public string VersionsDirectory => Path.Combine(StateDirectory, "versions");

    public string GenerationsDirectory => Path.Combine(StateDirectory, "generations");

    public string DesiredPath => Path.Combine(StateDirectory, "desired.json");

    public string ActivePath => Path.Combine(StateDirectory, "active.json");

    public string HeartbeatPath => Path.Combine(StateDirectory, "heartbeat.json");

    public string FailurePath => Path.Combine(StateDirectory, "last-failure.json");

    public string HostStopPath => Path.Combine(StateDirectory, "host.stop");

    public string LockPath => Path.Combine(StateDirectory, "host.lock");

    public string HandoffsPath => Path.Combine(StateDirectory, "handoffs.jsonl");

    public string LogPath => Path.Combine(StateDirectory, "host-events.jsonl");

    public DesiredWorker StageAndRequest(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The monitor worker executable was not found.", source);
        }

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var versionDirectory = Path.Combine(VersionsDirectory, hash.ToLowerInvariant());
        Directory.CreateDirectory(versionDirectory);
        var stagedPath = Path.Combine(versionDirectory, "cccg-monitor.exe");
        if (!File.Exists(stagedPath) || !HashEquals(stagedPath, hash))
        {
            var temporary = Path.Combine(versionDirectory, $".worker.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(source, temporary, overwrite: true);
                if (!HashEquals(temporary, hash))
                {
                    throw new InvalidDataException("The staged worker hash does not match the source hash.");
                }

                File.Move(temporary, stagedPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        var desired = new DesiredWorker
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Sha256 = hash,
            StagedPath = stagedPath
        };
        Write(DesiredPath, desired);
        return desired;
    }

    public T? Read<T>(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    public void Write<T>(string path, T value) => MonitorWorkerControl.WriteJsonAtomic(path, value);

    public void Append<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(value));
    }

    public void Log(string eventName, IReadOnlyDictionary<string, object?>? data = null) =>
        Append(LogPath, new
        {
            observedAtUtc = DateTimeOffset.UtcNow,
            @event = eventName,
            data = data ?? new Dictionary<string, object?>()
        });

    public static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool ProcessMatches(int processId, string expectedPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            var actual = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actual)
                && string.Equals(
                    Path.GetFullPath(actual),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsStableActivation(ActiveWorker? active, string expectedHash) =>
        active is not null
        && string.Equals(active.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase)
        && active.Retiring is { Count: 0 };

    private static bool HashEquals(string path, string expected) =>
        string.Equals(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            expected,
            StringComparison.OrdinalIgnoreCase);
}
