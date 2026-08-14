using System.Diagnostics;
using System.Text;

namespace CCCG.Core.Dispatch;

public interface IProcessLauncher
{
    int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter);
}

public interface IProcessWaiter
{
    int? Pid { get; }

    int Wait(TimeSpan timeout);
}

public sealed class DispatchRunner
{
    private readonly DispatchJobStore store;
    private readonly IProcessLauncher launcher;
    private readonly Func<string, IReadOnlyList<Peer>> listPeers;
    private readonly string? grokHome;
    private readonly string? codexCommand;
    private readonly string? claudeCommand;
    private readonly DispatchBindingStore? bindings;
    private readonly InboxLedger? inbox;
    private readonly ILiveInputInjector injector;
    private readonly OwnerRegistry owners;
    private readonly TimeSpan writerPollInterval;
    private readonly TimeSpan writerWaitTimeout;
    private readonly TimeSpan deliverWaitTimeout;
    private readonly TimeSpan readBackTimeout;

    public DispatchRunner(
        DispatchJobStore store,
        Func<string, IReadOnlyList<Peer>> listPeers,
        IProcessLauncher? launcher = null,
        string? grokHome = null,
        string? codexCommand = null,
        string? claudeCommand = null,
        DispatchBindingStore? bindings = null,
        InboxLedger? inbox = null,
        TimeSpan? writerPollInterval = null,
        TimeSpan? writerWaitTimeout = null,
        ILiveInputInjector? injector = null,
        OwnerRegistry? owners = null,
        TimeSpan? deliverWaitTimeout = null,
        TimeSpan? readBackTimeout = null)
    {
        this.store = store;
        this.listPeers = listPeers;
        this.launcher = launcher ?? new FileProcessLauncher();
        this.grokHome = grokHome;
        this.codexCommand = codexCommand;
        this.claudeCommand = claudeCommand;
        this.bindings = bindings;
        this.inbox = inbox;
        this.injector = injector ?? new WindowsLiveInputInjector();
        this.owners = owners ?? new OwnerRegistry();
        this.writerPollInterval = writerPollInterval ?? TimeSpan.FromMilliseconds(250);
        this.writerWaitTimeout = writerWaitTimeout ?? TimeSpan.FromHours(2);
        this.deliverWaitTimeout = deliverWaitTimeout ?? TimeSpan.FromHours(2);
        this.readBackTimeout = readBackTimeout ?? TimeSpan.FromSeconds(5);
    }

    public DispatchJob Enqueue(
        string provider,
        string prompt,
        string? sessionId = null,
        string? cwd = null,
        bool allowNew = true,
        string? model = null,
        string? reasoningEffort = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        provider = NormalizeProvider(provider);
        var binding = bindings?.Load(provider, cwd);
        var selection = Select(provider, sessionId, cwd, allowNew, binding);
        var job = store.Create(selection, prompt);
        job.RequestedSessionId = sessionId;
        job.AllowNew = allowNew;
        job.Model = NormalizeOverride(model);
        job.ReasoningEffort = NormalizeOverride(reasoningEffort);
        job.AffinityKey = BuildAffinityKey(
            provider,
            sessionId ?? binding?.SessionId,
            selection.Cwd ?? cwd);
        store.Write(job);

        inbox?.Post(
            "claude",
            provider,
            prompt,
            fromProvider: "claude",
            toProvider: provider,
            toSessionId: selection.SessionId,
            jobId: job.JobId);
        return job;
    }

    public DispatchJob Dispatch(
        string provider,
        string prompt,
        string? sessionId = null,
        string? cwd = null,
        bool allowNew = true,
        string? model = null,
        string? reasoningEffort = null)
    {
        var job = Enqueue(
            provider,
            prompt,
            sessionId,
            cwd,
            allowNew,
            model,
            reasoningEffort);
        ThreadPool.QueueUserWorkItem(_ => Run(job.JobId));
        return job;
    }

    public DispatchJob MarkWorker(string jobId, int workerPid) =>
        store.Update(jobId, job => job.WorkerPid = workerPid);

    public DispatchJob Run(string jobId)
    {
        var job = store.Update(jobId, current => current.WorkerPid = Environment.ProcessId);
        if (IsTerminal(job.Status))
        {
            return job;
        }

        try
        {
            if (job.Provider == "claude"
                && (!string.IsNullOrWhiteSpace(job.Model)
                    || !string.IsNullOrWhiteSpace(job.ReasoningEffort)))
            {
                throw new InvalidOperationException(
                    "Claude does not support per-dispatch model or reasoningEffort overrides; remove both parameters.");
            }

            var binding = bindings?.Load(job.Provider, job.Cwd);
            var selection = Select(
                job.Provider,
                job.RequestedSessionId,
                job.Cwd,
                job.AllowNew,
                binding);
            string? plannedSessionId = null;
            if (selection.Action == DispatchAction.Create
                && selection.Provider is "grok" or "claude")
            {
                plannedSessionId = Guid.NewGuid().ToString();
                selection = selection with { SessionId = plannedSessionId };
            }

            job.SessionId = selection.SessionId;
            job.Cwd = selection.Cwd;
            job.Action = selection.Action.ToString().ToLowerInvariant();
            job.Reason = selection.Reason;
            store.Write(job);

            if (selection.WaitForWriterFree)
            {
                job.Reason = "Waiting for the live writer to become idle.";
                store.Write(job);
                WaitUntilWriterFree(selection.Provider, selection.SessionId);
                job = store.Require(job.JobId);
                if (IsTerminal(job.Status))
                {
                    return job;
                }

                job.Reason = "Live writer is idle; delivering the queued turn.";
                store.Write(job);
            }

            if (selection.Action == DispatchAction.Deliver)
            {
                return DeliverToOwner(job, selection);
            }

            if (selection.Action == DispatchAction.Inject)
            {
                return InjectLive(job, selection);
            }

            CrossProcessFileGate? lease = null;
            if (selection.Action != DispatchAction.Attach)
            {
                var gatePath = Path.Combine(
                    store.Root,
                    "..",
                    "leases",
                    CrossProcessFileGate.Key(job.AffinityKey ?? job.Provider + "|_") + ".lock");
                lease = CrossProcessFileGate.Acquire(gatePath, TimeSpan.FromHours(2));
                job = store.Require(jobId);
                if (IsTerminal(job.Status))
                {
                    lease.Dispose();
                    return job;
                }
            }

            try
            {

            // Read-back verification baseline: how many messages the target
            // grok session shows on disk before this resume turn runs.
            int? peerTurnsBefore = null;
            if (selection.Provider == "grok"
                && selection.Action == DispatchAction.Resume
                && !string.IsNullOrWhiteSpace(selection.SessionId))
            {
                peerTurnsBefore = FindGrokMessageCount(selection.SessionId);
                job.PeerTurnsBefore = peerTurnsBefore;
            }

            var command = selection.Provider switch
            {
                "grok" => ProviderCommand.BuildGrok(
                    selection.Action,
                    selection.Action == DispatchAction.Create ? null : selection.SessionId,
                    selection.Cwd ?? Environment.CurrentDirectory,
                    store.PromptPath(job.JobId),
                    grokHome,
                    plannedSessionId,
                    job.Model,
                    job.ReasoningEffort),
                "claude" => ProviderCommand.BuildClaude(
                    selection.Action,
                    selection.Action == DispatchAction.Create ? null : selection.SessionId,
                    selection.Cwd ?? Environment.CurrentDirectory,
                    store.PromptPath(job.JobId),
                    claudeCommand,
                    plannedSessionId),
                _ => ProviderCommand.BuildCodex(
                    selection.Action,
                    selection.SessionId,
                    selection.Cwd ?? Environment.CurrentDirectory,
                    store.PromptPath(job.JobId),
                    codexCommand,
                    job.Model,
                    job.ReasoningEffort)
            };
            var stdin = selection.Provider is "codex" or "claude"
                ? store.PromptPath(job.JobId)
                : null;
            var pid = launcher.Start(
                command,
                store.StdoutPath(job.JobId),
                store.StderrPath(job.JobId),
                stdin,
                out var waiter);

            job.Status = DispatchJobStatus.Running;
            job.Pid = pid;
            job.StartedAt = DateTimeOffset.UtcNow;
            store.Write(job);

            var exit = waiter.Wait(TimeSpan.FromHours(2));
            job = store.Require(job.JobId);
            job.ExitCode = exit;
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (exit != 0)
            {
                job.Status = DispatchJobStatus.Failed;
                job.Error = "Provider exited with code " + exit + ".";
                store.Write(job);
                return job;
            }

            var providerError = ProviderOutputParser.FindError(
                selection.Provider,
                store.StdoutPath(job.JobId));
            if (!string.IsNullOrWhiteSpace(providerError))
            {
                throw new InvalidDataException(providerError);
            }

            var resolvedSessionId = selection.SessionId
                ?? ProviderOutputParser.FindSessionId(
                    selection.Provider,
                    store.StdoutPath(job.JobId),
                    plannedSessionId);
            if (selection.Action == DispatchAction.Create
                && string.IsNullOrWhiteSpace(resolvedSessionId))
            {
                throw new InvalidDataException(
                    $"{selection.Provider} completed but did not expose the new session id; CCCG refused to create an unbound peer.");
            }

            if (peerTurnsBefore is int turnsBefore)
            {
                var turnsAfter = WaitForGrokTurnIncrease(selection.SessionId!, turnsBefore);
                job.PeerTurnsAfter = turnsAfter;
                if (turnsAfter is null || turnsAfter <= turnsBefore)
                {
                    store.Write(job);
                    throw new InvalidDataException(
                        $"grok exited successfully but session '{selection.SessionId}' still shows "
                        + $"{turnsAfter ?? turnsBefore} messages (was {turnsBefore}); the resumed turn "
                        + "was not recorded, so the delivery is unverified.");
                }
            }

            job.SessionId = resolvedSessionId;
            job.Status = DispatchJobStatus.Succeeded;
            if (!string.IsNullOrWhiteSpace(resolvedSessionId))
            {
                bindings?.Save(selection.Provider, resolvedSessionId, selection.Cwd, managedByCccg: true);
            }

            // Persist the terminal status BEFORE the inbox post: the provider
            // turn already ran, so a post-success bookkeeping failure must not
            // relabel the job Failed (the caller would retry a consumed turn).
            store.Write(job);
            inbox?.Post(
                selection.Provider,
                "claude",
                NonEmptyContent(ProviderOutputParser.CollectResponse(
                    selection.Provider,
                    store.StdoutPath(job.JobId),
                    2000)),
                fromProvider: selection.Provider,
                fromSessionId: resolvedSessionId,
                toProvider: "claude",
                jobId: job.JobId);
            return job;
            }
            finally
            {
                lease?.Dispose();
            }
        }
        catch (Exception exception)
        {
            job = store.Require(jobId);
            if (job.Status == DispatchJobStatus.Succeeded)
            {
                // The provider turn completed and Succeeded was persisted; a
                // failure after that point (e.g. inbox bookkeeping) must not
                // relabel a delivered turn as Failed.
                return job;
            }

            job.Status = DispatchJobStatus.Failed;
            job.Error = exception.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            store.Write(job);
            return job;
        }
    }

    public DispatchJob Status(string jobId)
    {
        var job = store.Require(jobId);
        if (!IsTerminal(job.Status)
            && job.WorkerPid is int workerPid
            && !ProcessIsAlive(workerPid))
        {
            return store.Update(jobId, current =>
            {
                if (IsTerminal(current.Status))
                {
                    return;
                }

                current.Status = DispatchJobStatus.Failed;
                current.Error = "Dispatch worker exited before recording a terminal result.";
                current.FinishedAt = DateTimeOffset.UtcNow;
            });
        }

        return job;
    }

    public object Collect(string jobId)
    {
        var job = Status(jobId);
        return new
        {
            job.JobId,
            job.Provider,
            job.SessionId,
            job.Cwd,
            job.Model,
            job.ReasoningEffort,
            job.Action,
            job.Status,
            job.Reason,
            job.ExitCode,
            job.Error,
            response = ProviderOutputParser.CollectResponse(
                job.Provider,
                store.StdoutPath(jobId))
        };
    }

    public object WaitAndCollect(string jobId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = Status(jobId);
            if (IsTerminal(job.Status))
            {
                return Collect(jobId);
            }

            Thread.Sleep(50);
        }

        return Collect(jobId);
    }

    private DispatchSelection Select(
        string provider,
        string? requestedSessionId,
        string? cwd,
        bool allowNew,
        DispatchBinding? binding)
    {
        // The owner registry is authoritative: a live owner daemon accepts
        // spooled turns even before its provider session is visible as a peer.
        var owned = owners.TryFind(provider, requestedSessionId ?? binding?.SessionId, cwd);
        if (owned is not null)
        {
            return new DispatchSelection(
                provider,
                owned.SessionId ?? requestedSessionId,
                cwd ?? owned.Cwd,
                Title: null,
                DispatchAction.Deliver,
                PeerSelector.OwnedDeliveryReason);
        }

        var peers = listPeers(provider);
        var preferredId = requestedSessionId ?? binding?.SessionId;
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var preferred = peers.FirstOrDefault(peer => string.Equals(
                peer.SessionId,
                preferredId,
                StringComparison.OrdinalIgnoreCase));
            if (preferred is not null
                && preferred.Status.StartsWith("live-", StringComparison.Ordinal))
            {
                if (provider == "claude")
                {
                    throw new InvalidOperationException(
                        $"Claude session '{preferredId}' is live in Desktop and owns its writer. "
                        + "Do not CLI-resume it. Match its title with mcp__ccd_session_mgmt__list_sessions, "
                        + "then deliver through mcp__ccd_session_mgmt__send_message; the Desktop session id returned there is authoritative.");
                }

                if (bindings?.IsManaged(provider, preferredId) == true)
                {
                    return new DispatchSelection(
                        provider,
                        preferredId,
                        cwd ?? preferred.Cwd,
                        preferred.Title,
                        DispatchAction.Resume,
                        "Queued behind the active CCCG-owned turn; the same session will resume when its writer is free.");
                }

                return new DispatchSelection(
                    provider,
                    preferredId,
                    cwd ?? preferred.Cwd,
                    preferred.Title,
                    PeerSelector.LiveAction(provider, preferred.Status, hasOwner: false),
                    PeerSelector.LiveReason(provider, preferred.Status, hasOwner: false),
                    Pid: preferred.Pid);
            }
        }

        return PeerSelector.Select(
            provider,
            peers,
            requestedSessionId,
            cwd,
            allowNew,
            binding?.SessionId,
            peer => owners.TryFind(peer.Provider, peer.SessionId, peer.Cwd) is not null);
    }

    private DispatchJob DeliverToOwner(DispatchJob job, DispatchSelection selection)
    {
        // Re-resolve by session id first, then by workspace: the owner's
        // RefreshSessionIdentity can swap the registered sessionId between
        // Select's lookup and this one, and that tiny window must not turn a
        // live owner into a false "released its lease" failure.
        var entry = owners.TryFindEntry(selection.Provider, selection.SessionId, selection.Cwd)
            ?? owners.TryFindEntry(selection.Provider, sessionId: null, selection.Cwd)
            ?? throw new InvalidOperationException(
                $"The CCCG owner for {selection.Provider} session '{selection.SessionId}' "
                + "released its lease before delivery.");
        var owner = entry.Registration;
        var spool = new OwnerSpool(owner.SpoolDir);
        // Spool the RAW prompt: OwnerDaemon.BuildTurnText adds the single
        // "[CCCG message from <role> <session>]" sender label, so the wrapped
        // prompt.txt header must never reach the provider a second time.
        // (Fallback covers jobs created before prompt.raw.txt existed.)
        var rawPromptPath = store.RawPromptPath(job.JobId);
        spool.Post(new OwnerMessage(
            job.JobId,
            "claude",
            FromSessionId: null,
            File.ReadAllText(
                File.Exists(rawPromptPath) ? rawPromptPath : store.PromptPath(job.JobId)),
            DateTimeOffset.UtcNow));

        job.Status = DispatchJobStatus.Running;
        job.OwnerPid = owner.OwnerPid;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Reason = "Waiting for the CCCG owner daemon to run the delivered turn.";
        store.Write(job);

        var deadline = DateTimeOffset.UtcNow + deliverWaitTimeout;
        OwnerReceipt? receipt;
        while ((receipt = spool.TryReadReceipt(job.JobId)) is null)
        {
            // Liveness = the DeleteOnClose lease, not the PID: a reused PID
            // can look alive for the whole 2 h timeout while the owner is
            // long dead. The lease is released the instant the owner process
            // exits, so lease-held is authoritative (PID stays recorded on
            // the job for diagnostics only).
            if (!owners.LeaseIsHeld(entry.Key))
            {
                // One grace re-read: the owner may have written the receipt
                // just before exiting.
                receipt = spool.TryReadReceipt(job.JobId);
                if (receipt is null)
                {
                    throw new InvalidOperationException(
                        $"The CCCG owner daemon (pid {owner.OwnerPid}) exited before "
                        + "writing a receipt; the turn outcome is unknown — the owner may "
                        + "have died mid-turn after the provider consumed the message. "
                        + "Inspect the provider session before retrying.");
                }

                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"The CCCG owner did not confirm delivery within {deliverWaitTimeout}.");
            }

            job = store.Require(job.JobId);
            if (IsTerminal(job.Status))
            {
                return job;
            }

            Thread.Sleep(writerPollInterval);
        }

        job.ReceiptStatus = receipt.Status;
        job.DeliveredAt = receipt.DeliveredAt;
        job.FinishedAt = DateTimeOffset.UtcNow;
        if (receipt.Status != OwnerReceiptStatus.Delivered)
        {
            store.Write(job);
            throw new InvalidOperationException(
                receipt.Error ?? "The CCCG owner reported a failed delivery.");
        }

        File.WriteAllText(
            store.StdoutPath(job.JobId),
            receipt.ResponseText ?? string.Empty,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        job.Status = DispatchJobStatus.Succeeded;
        job.ExitCode = 0;
        // Persist Succeeded BEFORE the inbox post: the receipt proves the
        // provider ran the turn and the spool message is consumed, so nothing
        // after this point may downgrade the job to Failed.
        store.Write(job);
        inbox?.Post(
            selection.Provider,
            "claude",
            NonEmptyContent(ProviderOutputParser.CollectResponse(
                selection.Provider,
                store.StdoutPath(job.JobId),
                2000)),
            fromProvider: selection.Provider,
            fromSessionId: selection.SessionId,
            toProvider: "claude",
            jobId: job.JobId);
        return job;
    }

    /// <summary>
    /// Inbox content guard: <see cref="InboxLedger.Post"/> rejects null or
    /// whitespace, but a provider turn can legitimately produce an empty
    /// reply. The delivered turn must never be mislabeled Failed over it.
    /// </summary>
    private static string NonEmptyContent(string? content) =>
        string.IsNullOrWhiteSpace(content) ? "(empty response)" : content;

    private int? FindGrokMessageCount(string sessionId) =>
        listPeers("grok").FirstOrDefault(peer =>
                string.Equals(peer.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            ?.MessageCount;

    private int? WaitForGrokTurnIncrease(string sessionId, int before)
    {
        var deadline = DateTimeOffset.UtcNow + readBackTimeout;
        int? observed = null;
        while (true)
        {
            observed = FindGrokMessageCount(sessionId) ?? observed;
            if (observed > before || DateTimeOffset.UtcNow >= deadline)
            {
                return observed;
            }

            Thread.Sleep(writerPollInterval);
        }
    }

    private DispatchJob InjectLive(DispatchJob job, DispatchSelection selection)
    {
        var pid = selection.Pid
            ?? listPeers(selection.Provider).FirstOrDefault(peer =>
                    string.Equals(peer.SessionId, selection.SessionId, StringComparison.OrdinalIgnoreCase))
                ?.Pid;
        if (pid is null or <= 0)
        {
            throw new InvalidOperationException(
                $"No live process id for {selection.Provider} session '{selection.SessionId}'.");
        }

        var text = File.ReadAllText(store.PromptPath(job.JobId));
        job.Status = DispatchJobStatus.Running;
        job.Pid = pid;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Reason = "Typing into the live window as a new turn.";
        store.Write(job);

        injector.Inject(pid.Value, text);

        var receipt = "{\"text\":" + JsonEncode(
                $"Typed into the live {selection.Provider} window (pid {pid.Value}) as a new turn.")
            + "}";
        File.WriteAllText(store.StdoutPath(job.JobId), receipt);
        job.Status = DispatchJobStatus.Succeeded;
        job.ExitCode = 0;
        job.FinishedAt = DateTimeOffset.UtcNow;
        inbox?.Post(
            selection.Provider,
            "claude",
            ProviderOutputParser.CollectResponse(
                selection.Provider,
                store.StdoutPath(job.JobId),
                2000),
            fromProvider: selection.Provider,
            fromSessionId: selection.SessionId,
            toProvider: "claude",
            jobId: job.JobId);
        store.Write(job);
        return job;
    }

    private static string JsonEncode(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private void WaitUntilWriterFree(string provider, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + writerWaitTimeout;
        while (true)
        {
            var peer = listPeers(provider).FirstOrDefault(candidate =>
                string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (peer is null || peer.Status != PeerStatus.LiveWorking)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"{provider} session '{sessionId}' stayed live-working longer than {writerWaitTimeout}.");
            }

            Thread.Sleep(writerPollInterval);
        }
    }

    private static string BuildAffinityKey(string provider, string? sessionId, string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var workspace = Path.GetFullPath(cwd)
                .TrimEnd(Path.DirectorySeparatorChar)
                .ToLowerInvariant();
            return provider + "|cwd|" + workspace;
        }

        return !string.IsNullOrWhiteSpace(sessionId)
            ? provider + "|session|" + sessionId.Trim().ToLowerInvariant()
            : provider + "|cwd|_";
    }

    private static string NormalizeProvider(string provider)
    {
        provider = provider.Trim().ToLowerInvariant();
        return provider is "grok" or "codex" or "claude"
            ? provider
            : throw new ArgumentException("Provider must be grok, codex, or claude.");
    }

    private static string? NormalizeOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTerminal(string status) =>
        status is DispatchJobStatus.Succeeded or DispatchJobStatus.Failed;

    private static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed class FileProcessLauncher : IProcessLauncher
{
    public int Start(
        LaunchCommand command,
        string stdoutPath,
        string stderrPath,
        string? stdinPath,
        out IProcessWaiter waiter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinPath is not null,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            CreateNoWindow = true
        };
        if (stdinPath is not null)
        {
            info.StandardInputEncoding = utf8;
        }

        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start " + command.FileName);
        }

        var stdout = new StreamWriter(
            new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            utf8);
        var stderr = new StreamWriter(
            new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            utf8);
        process.OutputDataReceived += (_, args) => WriteLine(stdout, args.Data);
        process.ErrorDataReceived += (_, args) => WriteLine(stderr, args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (stdinPath is not null)
        {
            var stdinBytes = File.ReadAllBytes(stdinPath);
            process.StandardInput.BaseStream.Write(stdinBytes, 0, stdinBytes.Length);
            process.StandardInput.Close();
        }

        waiter = new ProcessWaiter(process, stdout, stderr);
        return process.Id;
    }

    private static void WriteLine(TextWriter writer, string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (writer)
        {
            writer.WriteLine(value);
            writer.Flush();
        }
    }

    private sealed class ProcessWaiter : IProcessWaiter
    {
        private readonly Process process;
        private readonly TextWriter stdout;
        private readonly TextWriter stderr;

        public ProcessWaiter(Process process, TextWriter stdout, TextWriter stderr)
        {
            this.process = process;
            this.stdout = stdout;
            this.stderr = stderr;
        }

        public int? Pid => process.Id;

        public int Wait(TimeSpan timeout)
        {
            try
            {
                if (!process.WaitForExit(timeout))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    throw new TimeoutException("Dispatch worker exceeded " + timeout + ".");
                }

                process.WaitForExit();
                return process.ExitCode;
            }
            finally
            {
                stdout.Dispose();
                stderr.Dispose();
                process.Dispose();
            }
        }
    }
}
