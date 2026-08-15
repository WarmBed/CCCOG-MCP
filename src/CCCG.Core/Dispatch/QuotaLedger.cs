using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class QuotaReservation
{
    internal QuotaReservation(
        string path,
        string source,
        string provider,
        string date,
        string fileName,
        bool allowed,
        int count,
        int limit,
        string message)
    {
        Path = path;
        Source = source;
        Provider = provider;
        Date = date;
        FileName = fileName;
        Allowed = allowed;
        Count = count;
        Limit = limit;
        Message = message;
    }

    internal string Path { get; }

    public string Source { get; }

    public string Provider { get; }

    public string Date { get; }

    public string FileName { get; }

    public bool Allowed { get; }

    public int Count { get; }

    public int Limit { get; }

    public string Message { get; }
}

/// <summary>
/// Cross-process local daily dispatch counter. Each source/provider/date has
/// one locked JSON counter file; no prompt or secret is written to its name.
/// Enforcement is opt-in through a positive quota environment variable; the
/// ledger keeps counting when neither variable is set.
/// </summary>
public sealed class QuotaLedger
{
    private const int RecordedClaudeLimit = 50;
    private const int RecordedDefaultLimit = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string root;
    private readonly Func<DateTimeOffset> now;
    private readonly int? claudeLimit;
    private readonly int? defaultLimit;

    public QuotaLedger(
        string? root = null,
        Func<DateTimeOffset>? now = null,
        int? claudeLimit = null,
        int? defaultLimit = null)
    {
        this.root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch",
            "quotas");
        this.now = now ?? (() => DateTimeOffset.Now);
        this.claudeLimit = claudeLimit ?? ParseLimit("CCCG_QUOTA_CLAUDE");
        this.defaultLimit = defaultLimit ?? ParseLimit("CCCG_QUOTA_DEFAULT");
        if (claudeLimit is <= 0 || defaultLimit is <= 0)
        {
            throw new InvalidOperationException("Quota limits must be positive.");
        }

        Directory.CreateDirectory(this.root);
    }

    public string Root => root;

    public QuotaReservation TryConsume(string source, string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        source = source.Trim();
        provider = provider.Trim().ToLowerInvariant();
        var date = now().ToLocalTime().ToString("yyyy-MM-dd");
        var fileName = HashKey(source, provider, date) + ".json";
        var directory = Path.Combine(root, date);
        var path = Path.Combine(directory, fileName);
        var enforcementLimit = provider == "claude" ? claudeLimit : defaultLimit;
        var recordedLimit = enforcementLimit
            ?? (provider == "claude" ? RecordedClaudeLimit : RecordedDefaultLimit);
        Directory.CreateDirectory(directory);

        using (CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30)))
        {
            var counter = Read(path, source, provider, date, recordedLimit);
            if (enforcementLimit is int limit && counter.Count >= limit)
            {
                return new QuotaReservation(
                    path,
                    source,
                    provider,
                    date,
                    fileName,
                    allowed: false,
                    counter.Count,
                    limit,
                    "CCCG daily quota exceeded for source=" + source
                    + ", provider=" + provider
                    + ": " + counter.Count + "/" + limit
                    + "; quota resets tomorrow.");
            }

            counter.Count++;
            counter.UpdatedAtUtc = DateTimeOffset.UtcNow;
            CrossProcessFileGate.AtomicWriteAllText(
                path,
                JsonSerializer.Serialize(counter, JsonOptions));
            return new QuotaReservation(
                path,
                source,
                provider,
                date,
                fileName,
                allowed: true,
                counter.Count,
                recordedLimit,
                "allowed");
        }
    }

    public void Release(QuotaReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (!reservation.Allowed)
        {
            return;
        }

        using (CrossProcessFileGate.Acquire(reservation.Path + ".lock", TimeSpan.FromSeconds(30)))
        {
            if (!File.Exists(reservation.Path))
            {
                return;
            }

            var counter = Read(
                reservation.Path,
                reservation.Source,
                reservation.Provider,
                reservation.Date,
                reservation.Limit);
            if (counter.Count > 0)
            {
                counter.Count--;
                counter.UpdatedAtUtc = DateTimeOffset.UtcNow;
                CrossProcessFileGate.AtomicWriteAllText(
                    reservation.Path,
                    JsonSerializer.Serialize(counter, JsonOptions));
            }
        }
    }

    private static Counter Read(
        string path,
        string source,
        string provider,
        string date,
        int limit)
    {
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Counter>(
                    File.ReadAllText(path),
                    JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Quota counter is not valid JSON: " + path,
                    exception);
            }
        }

        return new Counter
        {
            SchemaVersion = 1,
            Source = source,
            Provider = provider,
            Date = date,
            Limit = limit,
            Count = 0
        };
    }

    private static int? ParseLimit(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value.Trim(), out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException(variable + " must be a positive integer.");
        }

        return parsed;
    }

    private static string HashKey(string source, string provider, string date)
    {
        var bytes = Encoding.UTF8.GetBytes(source + "|" + provider + "|" + date);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class Counter
    {
        public int SchemaVersion { get; set; }

        public string Source { get; set; } = "";

        public string Provider { get; set; } = "";

        public string Date { get; set; } = "";

        public int Count { get; set; }

        public int Limit { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
