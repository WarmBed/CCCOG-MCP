namespace CCCG.Core.Dispatch;

public enum DispatchAction
{
    Attach,
    Resume,
    Create,
    Inject
}

public sealed record DispatchSelection(
    string Provider,
    string? SessionId,
    string? Cwd,
    string? Title,
    DispatchAction Action,
    string Reason,
    bool WaitForWriterFree = false,
    int? Pid = null);

public static class PeerSelector
{
    public static DispatchSelection Select(
        string provider,
        IReadOnlyList<Peer> peers,
        string? sessionId,
        string? cwd,
        bool allowNew,
        string? boundSessionId = null)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var match = peers.FirstOrDefault(peer =>
                string.Equals(peer.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"No {provider} session '{sessionId}'. Call cccg_list_peers and pick a live or resumable id.");
            }

            if (match.Status == PeerStatus.LiveWorking && provider == "claude")
            {
                throw new InvalidOperationException(
                    $"Claude session '{sessionId}' is live in Desktop and owns its writer. "
                    + "Do not CLI-resume it. Match its title with mcp__ccd_session_mgmt__list_sessions, "
                    + "then deliver through mcp__ccd_session_mgmt__send_message; the Desktop session id returned there is authoritative.");
            }

            return new DispatchSelection(
                provider,
                match.SessionId,
                FirstNonEmpty(cwd, match.Cwd),
                match.Title,
                LiveAction(provider, match.Status),
                LiveReason(provider, match.Status),
                Pid: match.Pid);
        }

        var ranked = PeerListing.FilterAndRank(peers, cwd, limit: 100);
        if (!string.IsNullOrWhiteSpace(boundSessionId))
        {
            var bound = ranked.FirstOrDefault(peer =>
                string.Equals(peer.SessionId, boundSessionId, StringComparison.OrdinalIgnoreCase)
                && peer.Status != PeerStatus.LiveWorking);
            if (bound is not null)
            {
                return new DispatchSelection(
                    provider,
                    bound.SessionId,
                    FirstNonEmpty(cwd, bound.Cwd),
                    bound.Title,
                    LiveAction(provider, bound.Status),
                    "Using the bound peer for this workspace.",
                    Pid: bound.Pid);
            }
        }

        var idle = ranked.FirstOrDefault(peer => peer.Status == PeerStatus.LiveIdle);
        if (idle is not null)
        {
            return new DispatchSelection(
                provider,
                idle.SessionId,
                FirstNonEmpty(cwd, idle.Cwd),
                idle.Title,
                LiveAction(provider, idle.Status),
                "Using a live-idle peer.",
                Pid: idle.Pid);
        }

        var resumable = ranked.FirstOrDefault(peer => peer.Status == PeerStatus.Resumable);
        if (resumable is not null)
        {
            return new DispatchSelection(
                provider,
                resumable.SessionId,
                FirstNonEmpty(cwd, resumable.Cwd),
                resumable.Title,
                DispatchAction.Resume,
                "No live-idle peer; resuming a matching session.");
        }

        if (!allowNew)
        {
            throw new InvalidOperationException(
                $"No matching {provider} peer. Pass sessionId or allow creating a new session.");
        }

        return new DispatchSelection(
            provider,
            SessionId: null,
            cwd,
            Title: null,
            DispatchAction.Create,
            "No live or resumable peer; creating a new session.");
    }

    internal static DispatchAction LiveAction(string provider, string status)
    {
        if (status.StartsWith("live-", StringComparison.Ordinal)
            && provider is "grok" or "codex")
        {
            return DispatchAction.Inject;
        }

        return status.StartsWith("live-", StringComparison.Ordinal)
            ? DispatchAction.Attach
            : DispatchAction.Resume;
    }

    internal static string LiveReason(string provider, string status)
    {
        if (provider is "grok" or "codex" && status.StartsWith("live-", StringComparison.Ordinal))
        {
            return "Typing into the live window as a new turn.";
        }

        return status == PeerStatus.LiveIdle
            ? "Delivering to the requested live-idle peer."
            : status == PeerStatus.LiveWorking
                ? "Inserting into the requested live peer."
                : "Resuming the requested peer.";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
