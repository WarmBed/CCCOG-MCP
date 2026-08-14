using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace CCCG.Dispatch;

[McpServerToolType]
public sealed class DispatchTools
{
    private readonly DispatchBackendClient backend;

    public DispatchTools(DispatchBackendClient backend)
    {
        this.backend = backend;
    }

    [McpServerTool(Name = "cccg_list_peers"),
     Description("List Grok, Codex, and Claude sessions. Named live Grok/Codex peers are typed into the open window as a new turn. Live Claude Desktop peers must use its native session-management send_message tool.")]
    public string ListPeers(
        [Description("Provider: grok, codex, claude, or all.")] string provider = "grok",
        [Description("Optional workspace/cwd filter.")] string? cwd = null,
        [Description("Maximum peers, 1-100.")] int limit = 20) =>
        Invoke("list", new { provider, cwd, limit });

    [McpServerTool(Name = "cccg_inspect_peer"),
     Description("Inspect one Grok, Codex, or Claude session without returning prompt/response content.")]
    public string InspectPeer(
        [Description("Session id from cccg_list_peers.")] string sessionId,
        [Description("Provider: grok, codex, claude, or all.")] string provider = "grok") =>
        Invoke("inspect", new { sessionId, provider });

    [McpServerTool(Name = "cccg_dispatch"),
     Description("Queue work and return a jobId immediately. Same-session work is serialized across all Claude Desktop sessions.")]
    public string Dispatch(
        [Description("Task for the peer.")] string prompt,
        [Description("Provider: grok, codex, or claude.")] string provider = "grok",
        [Description("Existing live, resumable, or CCCG-managed session id.")] string? sessionId = null,
        [Description("Workspace/cwd.")] string? cwd = null,
        [Description("Create a new bound session if none matches.")] bool allowNew = true) =>
        Invoke("dispatch", new { prompt, provider, sessionId, cwd, allowNew });

    [McpServerTool(Name = "cccg_dispatch_wait"),
     Description("Dispatch work and keep this tool call open until the peer finishes, so Claude receives the answer automatically without polling.")]
    public string DispatchWait(
        [Description("Task for the peer.")] string prompt,
        [Description("Provider: grok, codex, or claude.")] string provider = "grok",
        [Description("Existing live, resumable, or CCCG-managed session id.")] string? sessionId = null,
        [Description("Workspace/cwd.")] string? cwd = null,
        [Description("Create a new bound session if none matches.")] bool allowNew = true) =>
        Invoke("dispatchWait", new { prompt, provider, sessionId, cwd, allowNew });

    [McpServerTool(Name = "cccg_job_status"), Description("Read queued/running/succeeded/failed status.")]
    public string JobStatus([Description("Dispatch job id.")] string jobId) =>
        Invoke("jobStatus", new { jobId });

    [McpServerTool(Name = "cccg_job_collect"), Description("Collect the normalized peer response and session id.")]
    public string JobCollect([Description("Dispatch job id.")] string jobId) =>
        Invoke("jobCollect", new { jobId });

    [McpServerTool(Name = "cccg_inbox_post"), Description("Post a short shared mailbox note.")]
    public string InboxPost(
        [Description("Recipient role: claude, grok, or codex.")] string toRole,
        [Description("Plain-text note; do not include secrets.")] string content,
        [Description("Sender role.")] string fromRole = "claude",
        [Description("Recipient session id.")] string? toSessionId = null,
        [Description("Sender session id.")] string? fromSessionId = null) =>
        Invoke("inboxPost", new
        {
            toRole,
            content,
            fromRole,
            toSessionId,
            fromSessionId,
            fromProvider = fromRole,
            toProvider = toRole
        });

    [McpServerTool(Name = "cccg_inbox_list"), Description("List the shared cross-process mailbox.")]
    public string InboxList(
        [Description("Only pending notes.")] bool unreadOnly = false,
        [Description("Maximum notes.")] int limit = 20) =>
        Invoke("inboxList", new { unreadOnly, limit });

    [McpServerTool(Name = "cccg_inbox_ack"), Description("Mark one mailbox note read atomically.")]
    public string InboxAck([Description("Mailbox message id.")] string messageId) =>
        Invoke("inboxAck", new { messageId });

    [McpServerTool(Name = "cccg_runtime_status"),
     Description("Show the active versioned worker. Existing MCP connections pick up a newly installed worker on their next call.")]
    public string RuntimeStatus() => Invoke("runtimeStatus", new { });

    private string Invoke(string operation, object arguments)
    {
        try
        {
            return backend.Invoke(operation, arguments);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return JsonSerializer.Serialize(new { error = exception.Message });
        }
    }
}
