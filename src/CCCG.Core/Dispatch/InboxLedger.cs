using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class InboxMessage
{
    public string Id { get; set; } = "";
    public string FromRole { get; set; } = "";
    public string? FromProvider { get; set; }
    public string? FromSessionId { get; set; }
    public string ToRole { get; set; } = "";
    public string? ToProvider { get; set; }
    public string? ToSessionId { get; set; }
    public string Content { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InboxLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string path;
    private readonly string lockPath;

    public InboxLedger(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "inbox.jsonl");
        lockPath = path + ".lock";
    }

    public InboxMessage Post(
        string fromRole,
        string toRole,
        string content,
        string? fromProvider = null,
        string? fromSessionId = null,
        string? toProvider = null,
        string? toSessionId = null,
        string? jobId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(toRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var message = new InboxMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            FromRole = fromRole.Trim().ToLowerInvariant(),
            FromProvider = fromProvider,
            FromSessionId = fromSessionId,
            ToRole = toRole.Trim().ToLowerInvariant(),
            ToProvider = toProvider,
            ToSessionId = toSessionId,
            Content = content.Trim(),
            Status = "pending",
            JobId = jobId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        using (CrossProcessFileGate.Acquire(lockPath, TimeSpan.FromSeconds(30)))
        {
            File.AppendAllText(
                path,
                JsonSerializer.Serialize(message, JsonOptions) + Environment.NewLine,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return message;
    }

    public IReadOnlyList<InboxMessage> List(bool unreadOnly = false, int limit = 50)
    {
        limit = PeerListing.ClampLimit(limit);
        if (!File.Exists(path))
        {
            return Array.Empty<InboxMessage>();
        }

        List<InboxMessage> items;
        using (CrossProcessFileGate.Acquire(lockPath, TimeSpan.FromSeconds(30)))
        {
            items = File.ReadLines(path)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<InboxMessage>(line, JsonOptions))
                .OfType<InboxMessage>()
                .ToList();
        }

        if (unreadOnly)
        {
            items = items.Where(item => item.Status == "pending").ToList();
        }

        return items
            .OrderByDescending(item => item.CreatedAt)
            .Take(limit)
            .ToList();
    }

    public InboxMessage? Ack(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        using (CrossProcessFileGate.Acquire(lockPath, TimeSpan.FromSeconds(30)))
        {
            if (!File.Exists(path))
            {
                return null;
            }

            InboxMessage? updated = null;
            var rewritten = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var item = JsonSerializer.Deserialize<InboxMessage>(line, JsonOptions);
                if (item is not null && string.Equals(item.Id, messageId, StringComparison.OrdinalIgnoreCase))
                {
                    item.Status = "read";
                    updated = item;
                }

                if (item is not null)
                {
                    rewritten.Add(JsonSerializer.Serialize(item, JsonOptions));
                }
            }

            CrossProcessFileGate.AtomicWriteAllText(
                path,
                string.Join(Environment.NewLine, rewritten)
                + (rewritten.Count > 0 ? Environment.NewLine : string.Empty));
            return updated;
        }
    }
}
