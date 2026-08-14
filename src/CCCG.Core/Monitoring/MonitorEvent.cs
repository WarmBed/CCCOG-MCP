using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CCCG.Core.Monitoring;

public sealed record MonitorEvent
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sourceTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SourceTime { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("level")]
    public string Level { get; init; } = "info";

    [JsonPropertyName("sessionKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionKey { get; init; }

    [JsonPropertyName("historical")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Historical { get; init; }

    [JsonPropertyName("data")]
    public IReadOnlyDictionary<string, object?> Data { get; init; } =
        new Dictionary<string, object?>();
}

public sealed class StablePseudonymizer
{
    private readonly byte[] salt;

    private StablePseudonymizer(byte[] salt)
    {
        this.salt = salt;
    }

    public static StablePseudonymizer LoadOrCreate(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, ".monitor-salt");
        byte[] salt;

        if (File.Exists(path))
        {
            salt = File.ReadAllBytes(path);
            if (salt.Length < 32)
            {
                throw new InvalidDataException("The monitor salt is invalid.");
            }
        }
        else
        {
            salt = RandomNumberGenerator.GetBytes(32);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(salt);
        }

        return new StablePseudonymizer(salt);
    }

    public string Session(string rawId) => Pseudonym("s", rawId);

    public string CliSession(string rawId) => Pseudonym("cs", rawId);

    public string Correlation(string rawId) => Pseudonym("c", rawId);

    public string ProcessPath(string rawPath) => Pseudonym("p", rawPath);

    private string Pseudonym(string prefix, string value)
    {
        using var hmac = new HMACSHA256(salt);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return prefix + "_" + Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }
}

public static class MonitorPathRedactor
{
    public static string Redact(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)
            && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + path[profile.Length..];
        }

        return path;
    }
}
