using CCCG.Core.Providers;

namespace CCCG.Core.Dispatch;

/// <summary>
/// The CCCG owner daemon for one provider session. It holds the ownership
/// lease, registers itself in the owner registry, tails its spool for
/// incoming messages, runs each as a provider turn, and writes a receipt only
/// after the provider turn completed. Killing the process releases the
/// DeleteOnClose lease, which invalidates the registration.
/// </summary>
public sealed class OwnerDaemon : IDisposable
{
    private readonly OwnerRegistry registry;
    private readonly IProviderTurnTransport transport;
    private readonly string provider;
    private readonly string cwd;
    private readonly TimeSpan pollInterval;
    private string? sessionId;
    private string? key;
    private FileStream? workspaceLease;
    private FileStream? lease;
    private OwnerSpool? spool;
    private OwnerRegistration? registration;
    private bool disposed;

    public OwnerDaemon(
        string provider,
        string cwd,
        string? sessionId,
        IProviderTurnTransport transport,
        OwnerRegistry? registry = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        ArgumentNullException.ThrowIfNull(transport);
        this.provider = provider.Trim().ToLowerInvariant();
        this.cwd = Path.GetFullPath(cwd);
        this.sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        this.transport = transport;
        this.registry = registry ?? new OwnerRegistry();
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public OwnerRegistration? Registration => registration;

    public OwnerRegistration Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (registration is not null)
        {
            return registration;
        }

        sessionId ??= transport.SessionId ?? Guid.NewGuid().ToString();

        // Workspace-scoped lease FIRST: the per-session key contains the
        // (possibly synthetic GUID) session id, so two owners started without
        // --session-id in the same cwd would otherwise never collide and
        // TryFind-by-workspace would return an arbitrary one. One workspace,
        // one owner.
        if (workspaceLease is null)
        {
            var workspaceKey = OwnerRegistry.Key(provider, cwd, sessionId: null);
            try
            {
                workspaceLease = registry.AcquireLease(workspaceKey);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Another CCCG owner daemon is already running for {provider} in "
                    + $"'{cwd}'. One workspace gets exactly one owner; stop the other "
                    + "daemon (or pick a different --cwd) first.",
                    exception);
            }
        }

        key = OwnerRegistry.Key(provider, cwd, sessionId);
        lease = registry.AcquireLease(key);
        spool = new OwnerSpool(registry.SpoolDirectory(key));
        FailAbandonedTurns(spool);
        registration = new OwnerRegistration(
            1,
            provider,
            sessionId,
            cwd,
            Environment.ProcessId,
            spool.Root,
            DateTimeOffset.UtcNow);
        registry.Register(key, registration);
        return registration;
    }

    /// <summary>
    /// Drains every currently spooled message, one provider turn each.
    /// Returns the number of messages handled (delivered or failed).
    /// </summary>
    public int ProcessPendingOnce()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (spool is null)
        {
            throw new InvalidOperationException("Call Start() before processing the spool.");
        }

        var handled = 0;
        foreach (var (path, message) in spool.ReadIncoming())
        {
            if (spool.TryReadReceipt(message.MessageId) is not null)
            {
                spool.Remove(path);
                continue;
            }

            // Claim the message BEFORE the provider turn: if this process dies
            // mid-turn, restart finds the file in processing\ and writes an
            // unknown-outcome failure instead of re-running the turn.
            string processingPath;
            try
            {
                processingPath = spool.MoveToProcessing(path);
            }
            catch (IOException)
            {
                // A concurrent rename/enumeration race; retry on the next poll.
                continue;
            }

            var labeled = BuildTurnText(message);
            try
            {
                var response = transport.RunTurnAsync(labeled).GetAwaiter().GetResult();
                RefreshSessionIdentity();
                spool.WriteReceipt(new OwnerReceipt(
                    message.MessageId,
                    OwnerReceiptStatus.Delivered,
                    DateTimeOffset.UtcNow,
                    response,
                    Error: null));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                spool.WriteReceipt(new OwnerReceipt(
                    message.MessageId,
                    OwnerReceiptStatus.Failed,
                    DeliveredAt: null,
                    ResponseText: null,
                    exception.Message));
            }

            spool.Remove(processingPath);
            handled++;
        }

        return handled;
    }

    /// <summary>
    /// Startup sweep: any message left in <c>processing</c> was claimed by a
    /// previous owner that died before writing a receipt. The provider may or
    /// may not have run it, so the only honest receipt is an unknown-outcome
    /// failure — never a silent replay.
    /// </summary>
    private static void FailAbandonedTurns(OwnerSpool spool)
    {
        foreach (var (path, message) in spool.ReadProcessing())
        {
            if (spool.TryReadReceipt(message.MessageId) is null)
            {
                spool.WriteReceipt(new OwnerReceipt(
                    message.MessageId,
                    OwnerReceiptStatus.Failed,
                    DeliveredAt: null,
                    ResponseText: null,
                    "unknown outcome: the previous owner died mid-turn; the provider may or "
                    + "may not have run this message. Inspect the session before resending."));
            }

            spool.Remove(path);
        }
    }

    public void Run(CancellationToken cancellationToken)
    {
        Start();
        while (!cancellationToken.IsCancellationRequested)
        {
            ProcessPendingOnce();
            cancellationToken.WaitHandle.WaitOne(pollInterval);
        }
    }

    public static string BuildTurnText(OwnerMessage message)
    {
        var sender = string.IsNullOrWhiteSpace(message.FromSessionId)
            ? message.FromRole
            : message.FromRole + " " + message.FromSessionId;
        return "[CCCG message from " + sender + "]\n\n" + message.Text.Trim();
    }

    private void RefreshSessionIdentity()
    {
        var actual = transport.SessionId;
        if (string.IsNullOrWhiteSpace(actual)
            || string.Equals(actual, sessionId, StringComparison.Ordinal)
            || key is null
            || registration is null)
        {
            return;
        }

        sessionId = actual;
        registration = registration with { SessionId = actual };
        registry.Register(key, registration);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (key is not null)
        {
            registry.Unregister(key);
        }

        lease?.Dispose();
        workspaceLease?.Dispose();
    }
}
