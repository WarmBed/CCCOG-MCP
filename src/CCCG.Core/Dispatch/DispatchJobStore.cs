using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class DispatchJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string root;

    public DispatchJobStore(string? root = null)
    {
        this.root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch",
            "jobs");
        Directory.CreateDirectory(this.root);
    }

    public string Root => root;

    /// <summary>
    /// Invoked after a Write/Update that leaves the job in a terminal
    /// status, outside the status.json lock. Terminal transitions happen
    /// from several call sites (run, cancel, reconcile, orphan sweep), so
    /// the store is the one choke point that sees all of them; the callback
    /// must be idempotent because a terminal job can be rewritten.
    /// </summary>
    public Action<DispatchJob>? TerminalWritten { get; set; }

    public DispatchJob Create(DispatchSelection selection, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var job = new DispatchJob
        {
            JobId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + "Z_" + Guid.NewGuid().ToString("N")[..8],
            Provider = selection.Provider,
            SessionId = selection.SessionId,
            Cwd = selection.Cwd,
            Action = selection.Action.ToString().ToLowerInvariant(),
            Status = DispatchJobStatus.Queued,
            Reason = selection.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
            PromptChars = prompt.Length
        };
        var directory = JobDirectory(job.JobId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(PromptPath(job.JobId), WrapPrompt(selection.Provider, prompt));
        // Raw copy without the dispatch header: the owner-daemon path adds its
        // own single "[CCCG message from ...]" label, so Deliver must spool
        // the unwrapped text or the provider sees two sender headers.
        File.WriteAllText(RawPromptPath(job.JobId), prompt.Trim());
        Write(job);
        return job;
    }

    public DispatchJob Require(string jobId)
    {
        var path = StatusPath(jobId);
        using var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Unknown job '{jobId}'.");
        }

        var job = JsonSerializer.Deserialize<DispatchJob>(File.ReadAllText(path), JsonOptions);
        return job ?? throw new InvalidDataException($"Job '{jobId}' is not valid JSON.");
    }

    public void Write(DispatchJob job)
    {
        var path = StatusPath(job.JobId);
        using (var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30)))
        {
            CrossProcessFileGate.AtomicWriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
        }

        NotifyIfTerminal(job);
    }

    public DispatchJob Update(string jobId, Action<DispatchJob> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var path = StatusPath(jobId);
        DispatchJob job;
        using (var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30)))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Unknown job '{jobId}'.");
            }

            job = JsonSerializer.Deserialize<DispatchJob>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Job '{jobId}' is not valid JSON.");
            update(job);
            CrossProcessFileGate.AtomicWriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
        }

        NotifyIfTerminal(job);
        return job;
    }

    private void NotifyIfTerminal(DispatchJob job)
    {
        if (TerminalWritten is null
            || job.Status is not (DispatchJobStatus.Succeeded or DispatchJobStatus.Failed))
        {
            return;
        }

        try
        {
            TerminalWritten(job);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A wake is a courtesy on top of an outcome already on disk.
        }
    }

    public string JobDirectory(string jobId) => Path.Combine(root, jobId);

    public string PromptPath(string jobId) => Path.Combine(JobDirectory(jobId), "prompt.txt");

    public string RawPromptPath(string jobId) => Path.Combine(JobDirectory(jobId), "prompt.raw.txt");

    public string StdoutPath(string jobId) => Path.Combine(JobDirectory(jobId), "stdout.log");

    public string StderrPath(string jobId) => Path.Combine(JobDirectory(jobId), "stderr.log");

    public string StatusPath(string jobId) => Path.Combine(JobDirectory(jobId), "status.json");

    /// <summary>
    /// Every job id with a status.json on disk, for a startup/periodic
    /// reconciliation sweep. Best-effort: an unreadable or racing directory
    /// listing simply yields fewer ids rather than throwing.
    /// </summary>
    public IReadOnlyList<string> ListJobIds()
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var jobId = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, "status.json")))
            {
                ids.Add(jobId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Deletes the on-disk directory of every job that is already terminal
    /// (succeeded/failed) and finished more than <paramref name="maxAge"/>
    /// ago. Queued/running jobs, unreadable directories, and directories
    /// another process is holding open are always left alone. Returns how
    /// many were removed. Exists because the job store grew unbounded (300+
    /// dirs / ~290MB of xhigh stdout logs within a month) and nothing ever
    /// reclaimed it.
    /// </summary>
    public int PruneTerminalJobs(TimeSpan maxAge, DateTimeOffset? now = null)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow) - maxAge;
        var pruned = 0;
        foreach (var jobId in ListJobIds())
        {
            DispatchJob job;
            try
            {
                job = Require(jobId);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                continue;
            }

            if (job.Status is not (DispatchJobStatus.Succeeded or DispatchJobStatus.Failed))
            {
                continue;
            }

            var finished = job.FinishedAt ?? job.CreatedAt;
            if (finished >= cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(JobDirectory(jobId), recursive: true);
                pruned++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A reader still holding status.json.lock, or a stdout pump
                // still flushing: not this sweep's problem, next one gets it.
            }
        }

        return pruned;
    }

    public string CollectExcerpt(string jobId, int maxChars = 8000)
    {
        var path = StdoutPath(jobId);
        if (!File.Exists(path))
        {
            return "";
        }

        var text = File.ReadAllText(path);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[^(maxChars)..];
    }

    private static string WrapPrompt(string provider, string prompt)
    {
        return "[CCCG dispatch from Claude Desktop to " + provider + "]\n\n" + prompt.Trim();
    }
}
