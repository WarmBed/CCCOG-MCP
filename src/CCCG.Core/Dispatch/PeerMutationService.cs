using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed record PeerMutationResult(
    string Status,
    string Provider,
    string SessionId,
    string? Title,
    string? ManifestPath,
    string Message,
    bool CliReadBack = false);

/// <summary>
/// Fail-closed peer metadata mutations.  This service deliberately exposes no
/// guessed Codex/Grok title writer; only a proven Claude custom-title append
/// may be enabled by a CLI read-back probe.
/// </summary>
public sealed class PeerMutationService
{
    private const int MaxTitleCharacters = 200;

    private readonly string codexHome;
    private readonly string grokHome;
    private readonly string claudeHome;
    private readonly OwnerRegistry owners;
    private readonly Func<string, string, bool>? liveProbe;
    private readonly Func<string, string, bool>? ownerProbe;
    private readonly Func<string, string, bool>? cliReadBack;

    public PeerMutationService(
        string? codexHome = null,
        string? grokHome = null,
        string? claudeHome = null,
        OwnerRegistry? owners = null,
        Func<string, string, bool>? liveProbe = null,
        Func<string, string, bool>? ownerProbe = null,
        Func<string, string, bool>? cliReadBack = null)
    {
        this.codexHome = codexHome ?? CodexHome.Resolve().Path;
        this.grokHome = grokHome ?? GrokHome.Resolve().Path;
        this.claudeHome = claudeHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        this.owners = owners ?? new OwnerRegistry();
        this.liveProbe = liveProbe;
        this.ownerProbe = ownerProbe;
        this.cliReadBack = cliReadBack;
    }

    public PeerMutationResult SetTitle(string provider, string sessionId, string title)
    {
        provider = NormalizeProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        title = title.Trim();
        if (title.Length > MaxTitleCharacters
            || title.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"title must be 1-{MaxTitleCharacters} characters without control characters.",
                nameof(title));
        }

        if (IsLive(provider, sessionId))
        {
            return Result("live_refused", provider, sessionId, null,
                "Live provider writers are inspection-only; title was not changed.");
        }

        if (IsOwned(provider, sessionId))
        {
            return Result("owner_refused", provider, sessionId, null,
                "A running CCCG owner owns this session; title was not changed.");
        }

        if (provider != "claude")
        {
            return Result("unsupported", provider, sessionId, null,
                $"set_title is unsupported for {provider}: no provider-supported rename contract.");
        }

        var path = FindClaudeTranscript(sessionId);
        if (path is null)
        {
            return Result("not_found", provider, sessionId, null,
                $"No closed Claude transcript for session '{sessionId}'.");
        }

        if (cliReadBack is null)
        {
            return Result("unsupported", provider, sessionId, null,
                "Claude title write requires a verified CLI read-back probe; none is available, so no write was performed.");
        }

        var gatePath = path + ".cccg-title.lock";
        using var gate = CrossProcessFileGate.Acquire(gatePath, TimeSpan.FromSeconds(30));
        if (IsLive(provider, sessionId) || IsOwned(provider, sessionId))
        {
            return Result("live_refused", provider, sessionId, null,
                "Session became live or owned before title write; no bytes were changed.");
        }

        var probePath = path + ".cccg-title-probe-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(path, probePath, overwrite: false);
            AppendCustomTitle(probePath, sessionId, title);
            if (!cliReadBack(probePath, title))
            {
                return Result("unsupported", provider, sessionId, null,
                    "Claude CLI read-back did not recognize custom-title; no provider transcript was changed.");
            }

            AppendCustomTitle(path, sessionId, title);
            if (!cliReadBack(path, title))
            {
                return Result("failed", provider, sessionId, title,
                    "Claude title was appended but CLI read-back failed; inspect the transcript before retrying.");
            }

            return Result("updated", provider, sessionId, title,
                "Claude custom-title appended and CLI read-back verified.", cliReadBack: true);
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private bool IsLive(string provider, string sessionId)
    {
        if (liveProbe is not null)
        {
            return liveProbe(provider, sessionId);
        }

        return provider switch
        {
            "codex" => CodexLockIsHeld(sessionId),
            "grok" => ActivePidIsAlive(Path.Combine(grokHome, "active_sessions.json"), sessionId),
            "claude" => ActivePidIsAlive(Path.Combine(claudeHome, "sessions"), sessionId),
            _ => false
        };
    }

    private bool IsOwned(string provider, string sessionId) =>
        ownerProbe?.Invoke(provider, sessionId)
        ?? owners.TryFind(provider, sessionId, cwd: null) is not null;

    private bool CodexLockIsHeld(string sessionId)
    {
        var path = Path.Combine(codexHome, "thread-writer-locks", sessionId + ".lock");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool ActivePidIsAlive(string path, string sessionId)
    {
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json"))
            {
                if (ActiveFileMatches(file, sessionId))
                {
                    return true;
                }
            }

            return false;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (string.Equals(String(row, "session_id"), sessionId,
                        StringComparison.OrdinalIgnoreCase)
                    && PidIsAlive(row))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        return false;
    }

    private static bool ActiveFileMatches(string path, string sessionId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return string.Equals(String(root, "sessionId"), sessionId,
                       StringComparison.OrdinalIgnoreCase)
                && PidIsAlive(root);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return false;
        }
    }

    private static bool PidIsAlive(JsonElement row)
    {
        if (!row.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var processId))
        {
            return false;
        }

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

    private string? FindClaudeTranscript(string sessionId)
    {
        var root = Path.Combine(claudeHome, "projects");
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(root, sessionId + ".jsonl", SearchOption.AllDirectories))
        {
            if (!path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => string.Equals(part, "cccg-archive", StringComparison.OrdinalIgnoreCase)))
            {
                return path;
            }
        }

        return null;
    }

    private static void AppendCustomTitle(string path, string sessionId, string title)
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "custom-title",
            customTitle = title,
            sessionId
        });
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(line);
    }

    private static PeerMutationResult Result(
        string status,
        string provider,
        string sessionId,
        string? title,
        string message,
        bool cliReadBack = false) =>
        new(status, provider, sessionId, title, null, message, cliReadBack);

    private static string NormalizeProvider(string provider) =>
        (provider ?? "").Trim().ToLowerInvariant();

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
