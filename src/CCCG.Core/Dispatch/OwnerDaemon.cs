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
        key = OwnerRegistry.Key(provider, cwd, sessionId);
        lease = registry.AcquireLease(key);
        spool = new OwnerSpool(registry.SpoolDirectory(key));
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

            spool.Remove(path);
            handled++;
        }

        return handled;
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
    }
}
