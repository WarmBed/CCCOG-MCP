using System.Text.Json;

namespace CCCG.Core.Dispatch;

/// <summary>
/// One registered Claude session that wants to be woken when CCCG jobs it
/// cares about reach a terminal state. Written by hooks/cccg-session-hook.js
/// at SessionStart into dispatch\watch\session-<id>.json.
/// </summary>
public sealed class WakeRegistration
{
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>
    /// Pid of the Claude engine process that owns the session, as seen by
    /// the SessionStart hook (its parent pid). The Host records its own
    /// parent pid on every job it dispatches; when the two match, the wake
    /// goes to exactly that session without any cwd guessing.
    /// </summary>
    public int? EnginePid { get; set; }

    /// <summary>Touched by the state hook on every human prompt; drives the recency filter.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>
/// The "push" half CCCG otherwise lacks. cccg_dispatch is fire-and-forget
/// and nothing in CCCG can start a turn in an idle Claude session -- but the
/// Claude Code engine can: a SessionStart hook may return absolute
/// watchPaths, the engine keeps a file watcher on them for the life of the
/// session, and a FileChanged hook configured with asyncRewake wakes the
/// model on exit code 2 with the hook's stderr as a system reminder.
///
/// So: when a job goes terminal, this writes dispatch\wake\&lt;sessionId&gt;\cccg_wake
/// for every session that should hear about it; the engine notices; the wake
/// hook (hooks/cccg-wake-hook.js) reads the file and exits 2 with a summary.
///
/// Routing: a job that recorded callerSessionId goes to exactly that session.
/// Otherwise every registered session whose cwd is the job's cwd or a parent
/// of it is woken (coordinators dispatch into worktrees under their own
/// project), which can over-wake sibling coordinators sharing a project --
/// the wake hook's per-session cap and cursor keep that bounded.
///
/// Idempotent per job via a marker file, because terminal writes happen from
/// several places (run, cancel, reconcile) and a job must wake a session once.
/// </summary>
public sealed class WakeNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public const string WakeFileName = "cccg_wake";

    private readonly string watchRoot;
    private readonly string wakeRoot;

    public WakeNotifier(string? dispatchRoot = null)
    {
        var root = dispatchRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch");
        watchRoot = Path.Combine(root, "watch");
        wakeRoot = Path.Combine(root, "wake");
    }

    public string WakeFilePath(string sessionId) =>
        Path.Combine(wakeRoot, SafeSegment(sessionId), WakeFileName);

    public string RegistrationPath(string sessionId) =>
        Path.Combine(watchRoot, "session-" + SafeSegment(sessionId) + ".json");

    public void Register(string sessionId, string? cwd, DateTimeOffset? now = null, int? enginePid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Directory.CreateDirectory(watchRoot);
        var registration = new WakeRegistration
        {
            SessionId = sessionId,
            Cwd = cwd,
            RegisteredAt = now ?? DateTimeOffset.UtcNow,
            LastSeenAt = now ?? DateTimeOffset.UtcNow,
            EnginePid = enginePid
        };
        CrossProcessFileGate.AtomicWriteAllText(
            RegistrationPath(sessionId),
            JsonSerializer.Serialize(registration, JsonOptions));
    }

    public IReadOnlyList<WakeRegistration> ListRegistrations()
    {
        if (!Directory.Exists(watchRoot))
        {
            return Array.Empty<WakeRegistration>();
        }

        var result = new List<WakeRegistration>();
        foreach (var path in Directory.EnumerateFiles(watchRoot, "session-*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<WakeRegistration>(File.ReadAllText(path), JsonOptions);
                if (item is not null && !string.IsNullOrWhiteSpace(item.SessionId))
                {
                    result.Add(item);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    /// <summary>
    /// How far back a session's last human prompt may be for the cwd
    /// fallback to still consider it a live coordinator. The dispatching
    /// session prompted at dispatch time by definition, so anything older
    /// than the job plus this slack is not the dispatcher.
    /// </summary>
    public static readonly TimeSpan FallbackRecency = TimeSpan.FromHours(24);

    /// <summary>Upper bound on sessions a single cwd-fallback wake fans out to.</summary>
    public const int FallbackMaxTargets = 5;

    /// <summary>
    /// Which registered sessions a terminal job should wake, most precise
    /// first: the job's callerSessionId; else the registration whose
    /// enginePid equals the job's callerEnginePid; else the cwd fallback --
    /// registered sessions whose cwd is the job's cwd or a parent of it,
    /// seen within FallbackRecency of the job's creation, newest first,
    /// capped at FallbackMaxTargets. The fallback exists for Hosts that
    /// predate pid stamping; the first live run of this feature woke every
    /// session sharing a project on one job, which is what the recency
    /// filter and cap are for.
    /// </summary>
    public IReadOnlyList<string> Targets(DispatchJob job, IReadOnlyList<WakeRegistration> registrations, DateTimeOffset? now = null)
    {
        if (!string.IsNullOrWhiteSpace(job.CallerSessionId))
        {
            return new[] { job.CallerSessionId };
        }

        if (job.CallerEnginePid is int enginePid)
        {
            var byPid = registrations
                .Where(registration => registration.EnginePid == enginePid)
                .OrderByDescending(registration => registration.LastSeenAt ?? registration.RegisteredAt)
                .Select(registration => registration.SessionId)
                .FirstOrDefault();
            if (byPid is not null)
            {
                return new[] { byPid };
            }
        }

        var jobCwd = Normalize(job.Cwd);
        if (jobCwd is null)
        {
            return Array.Empty<string>();
        }

        var reference = job.CreatedAt == default ? (now ?? DateTimeOffset.UtcNow) : job.CreatedAt;
        var cutoff = reference - FallbackRecency;
        return registrations
            .Where(registration => Normalize(registration.Cwd) is string sessionCwd
                && (jobCwd == sessionCwd
                    || jobCwd.StartsWith(sessionCwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(registration => (registration.LastSeenAt ?? registration.RegisteredAt) >= cutoff)
            .OrderByDescending(registration => registration.LastSeenAt ?? registration.RegisteredAt)
            .Select(registration => registration.SessionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(FallbackMaxTargets)
            .ToList();
    }

    /// <summary>
    /// Writes the wake file for every target of a terminal job, once per job.
    /// Returns the session ids woken. Never throws: waking is a courtesy on
    /// top of an outcome that is already durably recorded.
    /// </summary>
    public IReadOnlyList<string> Notify(DispatchJob job)
    {
        if (job.Status is not (DispatchJobStatus.Succeeded or DispatchJobStatus.Failed))
        {
            return Array.Empty<string>();
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(wakeRoot, "notified"));
            var marker = Path.Combine(wakeRoot, "notified", SafeSegment(job.JobId));
            if (File.Exists(marker))
            {
                return Array.Empty<string>();
            }

            var targets = Targets(job, ListRegistrations());
            var payload = JsonSerializer.Serialize(new
            {
                jobId = job.JobId,
                status = job.Status,
                cancelled = job.CancelledAt is not null,
                provider = job.Provider,
                model = job.Model,
                sessionId = job.SessionId,
                cwd = job.Cwd,
                callerLabel = job.CallerLabel,
                error = job.Error,
                retryCount = job.RetryCount,
                finishedAt = job.FinishedAt,
                wokenAt = DateTimeOffset.UtcNow
            }, JsonOptions);

            foreach (var sessionId in targets)
            {
                var path = WakeFilePath(sessionId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                CrossProcessFileGate.AtomicWriteAllText(path, payload);
            }

            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            return targets;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Drop registrations and markers older than <paramref name="maxAge"/>.</summary>
    public int Prune(TimeSpan maxAge, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow) - maxAge;
        var removed = 0;
        foreach (var directory in new[] { watchRoot, Path.Combine(wakeRoot, "notified") })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (directory == watchRoot && !name.StartsWith("session-", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return removed;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string SafeSegment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }
}