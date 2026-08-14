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
        using var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30));
        CrossProcessFileGate.AtomicWriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
    }

    public DispatchJob Update(string jobId, Action<DispatchJob> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var path = StatusPath(jobId);
        using var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Unknown job '{jobId}'.");
        }

        var job = JsonSerializer.Deserialize<DispatchJob>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Job '{jobId}' is not valid JSON.");
        update(job);
        CrossProcessFileGate.AtomicWriteAllText(path, JsonSerializer.Serialize(job, JsonOptions));
        return job;
    }

    public string JobDirectory(string jobId) => Path.Combine(root, jobId);

    public string PromptPath(string jobId) => Path.Combine(JobDirectory(jobId), "prompt.txt");

    public string RawPromptPath(string jobId) => Path.Combine(JobDirectory(jobId), "prompt.raw.txt");

    public string StdoutPath(string jobId) => Path.Combine(JobDirectory(jobId), "stdout.log");

    public string StderrPath(string jobId) => Path.Combine(JobDirectory(jobId), "stderr.log");

    public string StatusPath(string jobId) => Path.Combine(JobDirectory(jobId), "status.json");

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
