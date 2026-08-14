using CCCG.Core.Monitoring;
using CCCG.Host;

try
{
    var options = HostOptions.Parse(args);
    if (options.Command == "help")
    {
        PrintHelp();
        return 0;
    }

    var store = new SupervisorStore(options.StateDirectory);
    switch (options.Command)
    {
        case "run":
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
                return await new WorkerSupervisor(options, store).RunAsync(cancellation.Token);
            }
        case "update":
            return await UpdateAsync(options, store);
        case "status":
            return Status(store);
        case "stop":
            return await StopAsync(options, store);
        default:
            throw new ArgumentException($"Unknown command '{options.Command}'.");
    }
}
catch (Exception exception) when (exception is ArgumentException
                                  or IOException
                                  or UnauthorizedAccessException
                                  or InvalidDataException
                                  or InvalidOperationException)
{
    Console.Error.WriteLine("cccg-host: " + exception.Message);
    return 2;
}

static async Task<int> UpdateAsync(HostOptions options, SupervisorStore store)
{
    if (string.IsNullOrWhiteSpace(options.WorkerPath))
    {
        throw new ArgumentException("update requires --worker <cccg-monitor.exe>.");
    }

    var desired = store.StageAndRequest(options.WorkerPath);
    Console.WriteLine($"Staged worker SHA-256: {desired.Sha256}");
    Console.WriteLine($"Update request: {desired.RequestId}");

    var heartbeat = store.Read<HostHeartbeat>(store.HeartbeatPath);
    if (!HeartbeatIsLive(heartbeat))
    {
        Console.WriteLine("Host is not currently running; the update will apply on its next start.");
        return 0;
    }

    var deadline = DateTimeOffset.UtcNow + options.CommandWait;
    while (DateTimeOffset.UtcNow < deadline)
    {
        var active = store.Read<ActiveWorker>(store.ActivePath);
        if (SupervisorStore.IsStableActivation(active, desired.Sha256)
            && active is not null
            && SupervisorStore.ProcessMatches(active.WorkerPid, active.WorkerPath))
        {
            Console.WriteLine($"Update active: pid={active.WorkerPid} generation={active.Generation}");
            Console.WriteLine($"Dataset: {active.RunDirectory}");
            return 0;
        }

        var failure = store.Read<SupervisorFailure>(store.FailurePath);
        if (failure?.RequestId == desired.RequestId)
        {
            Console.Error.WriteLine("Update rejected; previous worker remains active.");
            Console.Error.WriteLine(failure.Error);
            return 2;
        }

        await Task.Delay(200);
    }

    Console.Error.WriteLine("Timed out waiting for the host to activate the update.");
    return 2;
}

static int Status(SupervisorStore store)
{
    var heartbeat = store.Read<HostHeartbeat>(store.HeartbeatPath);
    var desired = store.Read<DesiredWorker>(store.DesiredPath);
    var active = store.Read<ActiveWorker>(store.ActivePath);
    var hostRunning = HeartbeatIsLive(heartbeat);
    var workerRunning = active is not null
        && SupervisorStore.ProcessMatches(active.WorkerPid, active.WorkerPath);

    Console.WriteLine($"host:    {(hostRunning ? "running" : "stopped")}");
    if (heartbeat is not null)
    {
        Console.WriteLine($"hostPid: {heartbeat.HostPid}");
        Console.WriteLine($"seen:    {heartbeat.ObservedAtUtc:O}");
    }

    Console.WriteLine($"worker:  {(workerRunning ? "running" : "stopped")}");
    if (active is not null)
    {
        Console.WriteLine($"workerPid:  {active.WorkerPid}");
        Console.WriteLine($"generation: {active.Generation}");
        Console.WriteLine($"activeHash: {active.Sha256}");
        Console.WriteLine($"content:    {(active.ContentCapture ? "ON" : "OFF")}");
        Console.WriteLine($"dataset:    {active.RunDirectory}");
    }

    if (desired is not null)
    {
        Console.WriteLine($"desiredHash: {desired.Sha256}");
    }

    return hostRunning && workerRunning ? 0 : 2;
}

static async Task<int> StopAsync(HostOptions options, SupervisorStore store)
{
    var heartbeat = store.Read<HostHeartbeat>(store.HeartbeatPath);
    if (!HeartbeatIsLive(heartbeat))
    {
        Console.WriteLine("Host is not running.");
        return 0;
    }

    MonitorWorkerControl.RequestStop(store.HostStopPath);
    var deadline = DateTimeOffset.UtcNow + options.CommandWait;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (!SupervisorStore.ProcessMatches(heartbeat!.HostPid, heartbeat.HostPath))
        {
            Console.WriteLine("Host and supervised worker stopped.");
            return 0;
        }

        await Task.Delay(200);
    }

    Console.Error.WriteLine("Timed out waiting for the host to stop cooperatively.");
    return 2;
}

static bool HeartbeatIsLive(HostHeartbeat? heartbeat) =>
    heartbeat is not null
    && DateTimeOffset.UtcNow - heartbeat.ObservedAtUtc < TimeSpan.FromSeconds(5)
    && SupervisorStore.ProcessMatches(heartbeat.HostPid, heartbeat.HostPath);

static void PrintHelp()
{
    Console.WriteLine("""
        cccg-host - zero-touch monitor worker supervisor

        Commands:
          cccg-host run --worker <cccg-monitor.exe> [monitor options]
          cccg-host update --worker <new-cccg-monitor.exe> [--wait <seconds>]
          cccg-host status
          cccg-host stop [--wait <seconds>]

        Shared options:
          --state-dir <path>        Supervisor state (default: %LOCALAPPDATA%\CCCG\host)
          --wait <seconds>          update/stop wait timeout (default: 30)

        Run options:
          --data-dir <path>         Monitor dataset root
          --main-log <path>         Override Desktop main.log
          --session-root <path>     Override/add session metadata root (repeatable)
          --transcript-root <path>  Override transcript root
          --history <lines>         Normalize recent main.log history (default: 2000)
          --capture-content         Save newly appended user/assistant text locally
          --ready-timeout <seconds> Candidate readiness timeout (default: 20)
          --stop-timeout <seconds>  Old-worker cooperative stop timeout (default: 10)
          --poll-ms <milliseconds>  Desired-version polling interval (default: 500)
          --overlap-ms <milliseconds> Ready-worker overlap/soak window (default: 1000)
          --no-dashboard            Run supervisor without its terminal dashboard

        An update never overwrites a running executable. The candidate is copied into an
        immutable SHA-256 directory, started in parallel, and promoted only after its
        ready file and dataset validate. Claude Desktop is never stopped or modified.
        """);
}
