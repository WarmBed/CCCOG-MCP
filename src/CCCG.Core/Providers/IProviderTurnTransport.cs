namespace CCCG.Core.Providers;

/// <summary>
/// A stateful, long-lived connection to one provider session. The transport
/// keeps the provider child's stdin open across turns; each
/// <see cref="RunTurnAsync"/> call is one full turn whose returned text is the
/// provider's reply. Delivery is confirmed only when this method returns.
/// </summary>
public interface IProviderTurnTransport : IAsyncDisposable
{
    /// <summary>
    /// The provider-native session identity (Codex thread id, Grok session
    /// id) once known. Null until the first turn establishes it.
    /// </summary>
    string? SessionId { get; }

    Task<string> RunTurnAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owner-daemon transport for Codex: wraps the persistent
/// <c>codex app-server</c> JSON-RPC client, which already serializes turns and
/// keeps stdin open for the process lifetime.
/// </summary>
public sealed class CodexOwnerTurnTransport : IProviderTurnTransport
{
    private readonly PersistentCodexAppServerClient client;
    private readonly string model;
    private readonly string? reasoningEffort;
    private readonly string cwd;

    public CodexOwnerTurnTransport(
        PersistentCodexAppServerClient client,
        string model,
        string? reasoningEffort,
        string cwd)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        this.client = client;
        this.model = model;
        this.reasoningEffort = reasoningEffort;
        this.cwd = cwd;
    }

    public string? SessionId => client.ThreadId;

    public async Task<string> RunTurnAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var completion = await client.CompleteAsync(
            text,
            model,
            reasoningEffort,
            cwd,
            outputSchema: null,
            cancellationToken).ConfigureAwait(false);
        return completion.Text;
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

/// <summary>
/// Placeholder for the Grok ACP transport (<c>grok agent stdio</c>).
/// docs/provider-adapters.md names the entrypoint but does not document the
/// ACP message schema, so CCCG refuses to guess a wire protocol. See
/// docs/path-a-owned-sessions.md ("Grok transport status").
/// </summary>
public sealed class UnimplementedGrokTurnTransport : IProviderTurnTransport
{
    public const string Reason =
        "The Grok ACP transport is not implemented: 'grok agent stdio' is named in "
        + "docs/provider-adapters.md but its message schema is not documented, and CCCG "
        + "does not guess wire protocols. Use the resume path for grok, or implement "
        + "IProviderTurnTransport against the verified ACP contract.";

    public string? SessionId => null;

    public Task<string> RunTurnAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException(Reason));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
