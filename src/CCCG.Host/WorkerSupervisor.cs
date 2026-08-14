using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CCCG.Core.Monitoring;

namespace CCCG.Host;

internal sealed class WorkerSupervisor
{
    private readonly HostOptions options;
    private readonly SupervisorStore store;
    private readonly string configurationKey;
    private ActiveRuntime? active;
    private string? failedRequestId;
    private DateTimeOffset nextRestartAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastRender = DateTimeOffset.MinValue;

    public WorkerSupervisor(HostOptions options, SupervisorStore store)
    {
        this.options = options;
        this.store = store;
        configurationKey = ComputeConfigurationKey(options);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);
        using var hostLock = AcquireLock();
        if (File.Exists(store.HostStopPath))
        {
            File.Delete(store.HostStopPath);
        }

        if (options.WorkerPath is { Length: > 0 } initialWorker)
        {
            var requested = store.StageAndRequest(initialWorker);
            store.Log("worker.requested", BasicDescriptor(requested));
        }

        var desired = store.Read<DesiredWorker>(store.DesiredPath)
            ?? throw new InvalidOperationException(
                "No desired worker is configured. Start with --worker <cccg-monitor.exe>.");
        active = TryAttachActive();
        if (NeedsSwitch(active, desired))
        {
            await TrySwitchAsync(desired, cancellationToken);
        }

        while (!cancellationToken.IsCancellationRequested && !File.Exists(store.HostStopPath))
        {
            if (active is not null && !SupervisorStore.ProcessMatches(
                    active.State.WorkerPid,
                    active.State.WorkerPath))
            {
                store.Log("worker.exited_unexpectedly", new Dictionary<string, object?>
                {
                    ["generation"] = active.State.Generation,
                    ["workerPid"] = active.State.WorkerPid
                });
                active = null;
                nextRestartAt = DateTimeOffset.UtcNow.AddSeconds(2);
            }

            desired = store.Read<DesiredWorker>(store.DesiredPath) ?? desired;
            var needsSwitch = NeedsSwitch(active, desired);
            var mayRetry = desired.RequestId != failedRequestId
                || (active is null && DateTimeOffset.UtcNow >= nextRestartAt);
            if (needsSwitch && mayRetry)
            {
                await TrySwitchAsync(desired, cancellationToken);
            }
            else if (!needsSwitch
                     && active is not null
                     && active.State.RequestId != desired.RequestId)
            {
                var acknowledged = active.State with { RequestId = desired.RequestId };
                active = new ActiveRuntime(acknowledged, active.Process);
                store.Write(store.ActivePath, acknowledged);
                TryDelete(store.FailurePath);
                failedRequestId = null;
            }

            WriteHeartbeat();
            if (!options.NoDashboard)
            {
                Render(desired);
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (active is not null)
        {
            await StopWorkerAsync(active.State, options.StopTimeout, cancellationToken: default);
            active = null;
        }

        TryDelete(store.ActivePath);
        TryDelete(store.HeartbeatPath);
        TryDelete(store.HostStopPath);
        store.Log("host.stopped");
        return 0;
    }

    private FileStream AcquireLock()
    {
        try
        {
            return new FileStream(
                store.LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another cccg-host is already running for this state directory.", exception);
        }
    }

    private ActiveRuntime? TryAttachActive()
    {
        var state = store.Read<ActiveWorker>(store.ActivePath);
        if (state is null
            || !SupervisorStore.ProcessMatches(state.WorkerPid, state.WorkerPath)
            || !ReadyMatches(
                state.ReadyFile,
                state.Generation,
                state.WorkerPid,
                state.ContentCapture,
                expectedDataDirectory: null,
                out var attachedReady)
            || attachedReady is null
            || !string.Equals(
                Path.GetFullPath(attachedReady.RunDirectory),
                Path.GetFullPath(state.RunDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var retiring in state.Retiring)
        {
            CleanupRetiring(retiring);
        }

        if (state.Retiring.Count > 0)
        {
            state = state with { Retiring = Array.Empty<RetiringWorker>() };
            store.Write(store.ActivePath, state);
        }

        store.Log("worker.attached", new Dictionary<string, object?>
        {
            ["generation"] = state.Generation,
            ["workerPid"] = state.WorkerPid
        });
        return new ActiveRuntime(state, null);
    }

    private async Task TrySwitchAsync(DesiredWorker desired, CancellationToken cancellationToken)
    {
        var previous = active;
        var launch = await LaunchCandidateAsync(desired, cancellationToken);
        if (launch.Runtime is null)
        {
            failedRequestId = desired.RequestId;
            nextRestartAt = DateTimeOffset.UtcNow.AddSeconds(5);
            var failure = new SupervisorFailure
            {
                RequestId = desired.RequestId,
                Sha256 = desired.Sha256,
                Error = launch.Error ?? "Candidate failed without an error message."
            };
            store.Write(store.FailurePath, failure);
            store.Log("worker.candidate_failed", new Dictionary<string, object?>
            {
                ["requestId"] = desired.RequestId,
                ["sha256"] = desired.Sha256,
                ["error"] = failure.Error,
                ["previousKeptRunning"] = previous is not null
            });
            return;
        }

        var candidate = launch.Runtime;
        if (previous is not null && options.HandoffOverlap > TimeSpan.Zero)
        {
            store.Log("handoff.overlap_started", new Dictionary<string, object?>
            {
                ["candidateGeneration"] = candidate.State.Generation,
                ["candidateWorkerPid"] = candidate.State.WorkerPid,
                ["previousWorkerPid"] = previous?.State.WorkerPid,
                ["overlapMilliseconds"] = options.HandoffOverlap.TotalMilliseconds
            });
            try
            {
                await Task.Delay(options.HandoffOverlap, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                StopCandidate(candidate.Process!, candidate.State.StopFile);
                return;
            }

            if (!SupervisorStore.ProcessMatches(
                    candidate.State.WorkerPid,
                    candidate.State.WorkerPath))
            {
                failedRequestId = desired.RequestId;
                var failure = new SupervisorFailure
                {
                    RequestId = desired.RequestId,
                    Sha256 = desired.Sha256,
                    Error = "Candidate exited during the handoff overlap window."
                };
                store.Write(store.FailurePath, failure);
                store.Log("worker.candidate_failed", new Dictionary<string, object?>
                {
                    ["requestId"] = desired.RequestId,
                    ["sha256"] = desired.Sha256,
                    ["error"] = failure.Error,
                    ["previousKeptRunning"] = previous is not null
                });
                candidate.Process?.Dispose();
                return;
            }
        }

        var retiring = previous is null
            ? Array.Empty<RetiringWorker>()
            : new[]
            {
                new RetiringWorker
                {
                    Generation = previous.State.Generation,
                    WorkerPid = previous.State.WorkerPid,
                    WorkerPath = previous.State.WorkerPath,
                    ReadyFile = previous.State.ReadyFile,
                    StopFile = previous.State.StopFile
                }
            };
        var activating = candidate.State with { Retiring = retiring };
        store.Write(store.ActivePath, activating);
        active = new ActiveRuntime(activating, candidate.Process);

        var stoppedCooperatively = true;
        if (previous is not null)
        {
            stoppedCooperatively = await StopWorkerAsync(
                previous.State,
                options.StopTimeout,
                cancellationToken);
        }

        var stable = activating with { Retiring = Array.Empty<RetiringWorker>() };
        store.Write(store.ActivePath, stable);
        active = new ActiveRuntime(stable, candidate.Process);
        TryDelete(store.FailurePath);
        failedRequestId = null;
        store.Append(store.HandoffsPath, new HandoffRecord
        {
            NewGeneration = stable.Generation,
            NewWorkerPid = stable.WorkerPid,
            NewSha256 = stable.Sha256,
            PreviousGeneration = previous?.State.Generation,
            PreviousWorkerPid = previous?.State.WorkerPid,
            PreviousStoppedCooperatively = stoppedCooperatively,
            NewRunDirectory = stable.RunDirectory,
            CandidateReadyAtUtc = stable.ReadyAtUtc,
            OverlapMilliseconds = previous is null ? 0 : options.HandoffOverlap.TotalMilliseconds
        });
        store.Log("handoff.completed", new Dictionary<string, object?>
        {
            ["newGeneration"] = stable.Generation,
            ["newWorkerPid"] = stable.WorkerPid,
            ["previousWorkerPid"] = previous?.State.WorkerPid,
            ["previousStoppedCooperatively"] = stoppedCooperatively
        });
    }

    private async Task<LaunchResult> LaunchCandidateAsync(
        DesiredWorker desired,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(desired.StagedPath) || !HashEquals(desired.StagedPath, desired.Sha256))
        {
            return new LaunchResult(null, "The staged worker is missing or its SHA-256 has changed.");
        }

        var generation = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'")
            + "_" + Guid.NewGuid().ToString("N")[..8];
        var controlDirectory = Path.Combine(store.GenerationsDirectory, generation);
        Directory.CreateDirectory(controlDirectory);
        var readyFile = Path.Combine(controlDirectory, "ready.json");
        var stopFile = Path.Combine(controlDirectory, "stop.signal");

        var startInfo = new ProcessStartInfo
        {
            FileName = desired.StagedPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(desired.StagedPath)!
        };
        AddWorkerArguments(startInfo, generation, readyFile, stopFile);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) => LogWorkerLine(
                "worker.stdout",
                generation,
                eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => LogWorkerLine(
                "worker.stderr",
                generation,
                eventArgs.Data);
            if (!process.Start())
            {
                process.Dispose();
                return new LaunchResult(null, "The candidate worker process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or IOException)
        {
            process?.Dispose();
            return new LaunchResult(null, $"Candidate launch failed: {exception.Message}");
        }

        store.Log("worker.candidate_started", new Dictionary<string, object?>
        {
            ["generation"] = generation,
            ["workerPid"] = process.Id,
            ["sha256"] = desired.Sha256
        });

        var deadline = DateTimeOffset.UtcNow + options.ReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                process.Dispose();
                return new LaunchResult(null, $"Candidate exited before ready (exit code {exitCode}).");
            }

            if (File.Exists(readyFile))
            {
                if (!ReadyMatches(
                        readyFile,
                        generation,
                        process.Id,
                        options.CaptureContent,
                        options.DataDirectory,
                        out var ready)
                    || ready is null)
                {
                    StopCandidate(process, stopFile);
                    return new LaunchResult(null, "Candidate readiness data failed validation.");
                }

                var activeState = new ActiveWorker
                {
                    RequestId = desired.RequestId,
                    Generation = generation,
                    Sha256 = desired.Sha256,
                    ConfigurationKey = configurationKey,
                    WorkerPath = desired.StagedPath,
                    WorkerPid = process.Id,
                    ReadyFile = readyFile,
                    StopFile = stopFile,
                    RunDirectory = ready.RunDirectory,
                    ContentCapture = ready.ContentCapture,
                    ReadyAtUtc = ready.ReadyAtUtc
                };
                store.Log("worker.candidate_ready", new Dictionary<string, object?>
                {
                    ["generation"] = generation,
                    ["workerPid"] = process.Id,
                    ["runDirectory"] = ready.RunDirectory
                });
                return new LaunchResult(new ActiveRuntime(activeState, process), null);
            }

            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        StopCandidate(process, stopFile);
        return new LaunchResult(null, "Candidate did not become ready before the timeout.");
    }

    private void AddWorkerArguments(
        ProcessStartInfo startInfo,
        string generation,
        string readyFile,
        string stopFile)
    {
        foreach (var value in new[]
                 {
                     "watch", "--history", options.HistoryLines.ToString(
                         System.Globalization.CultureInfo.InvariantCulture),
                     "--data-dir", options.DataDirectory,
                     "--no-dashboard", "--quiet",
                     "--ready-file", readyFile,
                     "--stop-file", stopFile,
                     "--generation", generation
                 })
        {
            startInfo.ArgumentList.Add(value);
        }

        if (options.CaptureContent)
        {
            startInfo.ArgumentList.Add("--capture-content");
        }

        if (options.MainLog is { Length: > 0 } mainLog)
        {
            startInfo.ArgumentList.Add("--main-log");
            startInfo.ArgumentList.Add(mainLog);
        }

        foreach (var root in options.SessionRoots)
        {
            startInfo.ArgumentList.Add("--session-root");
            startInfo.ArgumentList.Add(root);
        }

        if (options.TranscriptRoot is { Length: > 0 } transcriptRoot)
        {
            startInfo.ArgumentList.Add("--transcript-root");
            startInfo.ArgumentList.Add(transcriptRoot);
        }
    }

    private bool ReadyMatches(
        string readyFile,
        string generation,
        int workerPid,
        bool expectedContentCapture,
        string? expectedDataDirectory,
        out MonitorWorkerReady? ready)
    {
        ready = MonitorWorkerControl.TryReadReady(readyFile);
        if (ready is null
            || ready.SchemaVersion != 1
            || ready.Generation != generation
            || ready.WorkerPid != workerPid
            || ready.ContentCapture != expectedContentCapture
            || !Directory.Exists(ready.RunDirectory)
            || !File.Exists(Path.Combine(ready.RunDirectory, "manifest.json"))
            || !File.Exists(Path.Combine(ready.RunDirectory, "events.jsonl")))
        {
            return false;
        }

        if (expectedDataDirectory is not null)
        {
            var dataRoot = Path.GetFullPath(expectedDataDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var runRoot = Path.GetFullPath(ready.RunDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!runRoot.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(ready.RunDirectory, "manifest.json")));
            var root = manifest.RootElement;
            return root.TryGetProperty("mode", out var mode)
                && mode.GetString() == "watch"
                && root.TryGetProperty("privacy", out var privacy)
                && privacy.TryGetProperty("promptBodiesStored", out var promptBodies)
                && promptBodies.ValueKind == (expectedContentCapture
                    ? JsonValueKind.True
                    : JsonValueKind.False)
                && privacy.TryGetProperty("responseBodiesStored", out var responseBodies)
                && responseBodies.ValueKind == (expectedContentCapture
                    ? JsonValueKind.True
                    : JsonValueKind.False);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> StopWorkerAsync(
        ActiveWorker worker,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        MonitorWorkerControl.RequestStop(worker.StopFile);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (!SupervisorStore.ProcessExists(worker.WorkerPid))
            {
                return true;
            }

            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (SupervisorStore.ProcessMatches(worker.WorkerPid, worker.WorkerPath))
        {
            try
            {
                using var process = Process.GetProcessById(worker.WorkerPid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
            {
            }
        }

        return false;
    }

    private void CleanupRetiring(RetiringWorker worker)
    {
        MonitorWorkerControl.RequestStop(worker.StopFile);
        if (!SupervisorStore.ProcessMatches(worker.WorkerPid, worker.WorkerPath))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(worker.WorkerPid);
            if (!process.WaitForExit((int)options.StopTimeout.TotalMilliseconds)
                && SupervisorStore.ProcessMatches(worker.WorkerPid, worker.WorkerPath))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
        }
    }

    private static void StopCandidate(Process process, string stopFile)
    {
        MonitorWorkerControl.RequestStop(stopFile);
        try
        {
            if (!process.WaitForExit(2000) && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void WriteHeartbeat() => store.Write(store.HeartbeatPath, new HostHeartbeat
    {
        HostPid = Environment.ProcessId,
        HostPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The host executable path is unavailable."),
        ActiveGeneration = active?.State.Generation,
        ActiveWorkerPid = active?.State.WorkerPid
    });

    private void Render(DesiredWorker desired)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastRender < TimeSpan.FromSeconds(1))
        {
            return;
        }

        lastRender = now;
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            return;
        }

        Console.WriteLine("CCCG HOST  hot-swap supervisor / Claude untouched");
        Console.WriteLine($"UTC {now:yyyy-MM-dd HH:mm:ss}  hostPid={Environment.ProcessId}");
        Console.WriteLine($"desired={ShortHash(desired.Sha256)}");
        if (active is null)
        {
            Console.WriteLine("worker: unavailable (retrying)");
        }
        else
        {
            var snapshot = ReadLastEvent(active.State.RunDirectory);
            Console.WriteLine(
                $"worker: pid={active.State.WorkerPid} generation={active.State.Generation} hash={ShortHash(active.State.Sha256)}");
            Console.WriteLine($"run:    {active.State.RunDirectory}");
            Console.WriteLine(
                $"content={(active.State.ContentCapture ? "ON" : "OFF")} lastEvent={snapshot.Event ?? "-"} session={snapshot.SessionKey ?? "-"} age={snapshot.AgeSeconds?.ToString() ?? "-"}s");
        }

        Console.WriteLine();
        Console.WriteLine("Updates are staged, health-checked, then handed off. Ctrl+C stops host and worker only.");
    }

    private static LastEventSnapshot ReadLastEvent(string runDirectory)
    {
        var path = Path.Combine(runDirectory, "events.jsonl");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = Math.Max(0, stream.Length - 65536);
            using var reader = new StreamReader(stream);
            if (stream.Position > 0)
            {
                reader.ReadLine();
            }

            string? line = null;
            while (reader.ReadLine() is { } next)
            {
                if (!string.IsNullOrWhiteSpace(next))
                {
                    line = next;
                }
            }

            if (line is null)
            {
                return new LastEventSnapshot(null, null, null);
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventName = root.TryGetProperty("event", out var eventElement)
                ? eventElement.GetString()
                : null;
            var sessionKey = root.TryGetProperty("sessionKey", out var sessionElement)
                ? sessionElement.GetString()
                : null;
            int? age = null;
            if (root.TryGetProperty("observedAtUtc", out var timeElement)
                && timeElement.TryGetDateTimeOffset(out var observed))
            {
                age = Math.Max(0, (int)(DateTimeOffset.UtcNow - observed).TotalSeconds);
            }

            return new LastEventSnapshot(eventName, sessionKey, age);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new LastEventSnapshot(null, null, null);
        }
    }

    private void LogWorkerLine(string eventName, string generation, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        store.Log(eventName, new Dictionary<string, object?>
        {
            ["generation"] = generation,
            ["line"] = line
        });
    }

    private static IReadOnlyDictionary<string, object?> BasicDescriptor(DesiredWorker desired) =>
        new Dictionary<string, object?>
        {
            ["requestId"] = desired.RequestId,
            ["sha256"] = desired.Sha256,
            ["stagedPath"] = desired.StagedPath
        };

    private static bool HashEquals(string path, string expected) =>
        string.Equals(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static bool HashesEqual(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private bool NeedsSwitch(ActiveRuntime? runtime, DesiredWorker desired) =>
        runtime is null
        || !HashesEqual(runtime.State.Sha256, desired.Sha256)
        || string.IsNullOrWhiteSpace(runtime.State.ConfigurationKey)
        || !HashesEqual(runtime.State.ConfigurationKey, configurationKey);

    private static string ComputeConfigurationKey(HostOptions options)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            dataDirectory = Path.GetFullPath(options.DataDirectory),
            mainLog = options.MainLog is null ? null : Path.GetFullPath(options.MainLog),
            sessionRoots = options.SessionRoots.Select(Path.GetFullPath).ToArray(),
            transcriptRoot = options.TranscriptRoot is null
                ? null
                : Path.GetFullPath(options.TranscriptRoot),
            options.HistoryLines,
            options.CaptureContent
        });
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ActiveRuntime(ActiveWorker State, Process? Process);

    private sealed record LaunchResult(ActiveRuntime? Runtime, string? Error);

    private sealed record LastEventSnapshot(string? Event, string? SessionKey, int? AgeSeconds);
}
