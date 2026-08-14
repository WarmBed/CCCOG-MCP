using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CCCG.Core.Dispatch;

public sealed class DispatchBinding
{
    public string Provider { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool ManagedByCccg { get; set; }
}

public sealed class DispatchBindingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string root;

    public DispatchBindingStore(string? root = null)
    {
        this.root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch",
            "bindings");
        Directory.CreateDirectory(this.root);
    }

    public void Save(string provider, string sessionId, string? cwd, bool managedByCccg = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var binding = new DispatchBinding
        {
            Provider = provider.Trim().ToLowerInvariant(),
            SessionId = sessionId,
            Cwd = cwd,
            UpdatedAt = DateTimeOffset.UtcNow,
            ManagedByCccg = managedByCccg
        };
        var path = PathFor(binding.Provider, cwd);
        using var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30));
        CrossProcessFileGate.AtomicWriteAllText(path, JsonSerializer.Serialize(binding, JsonOptions));
    }

    public DispatchBinding? Load(string provider, string? cwd)
    {
        var path = PathFor(provider.Trim().ToLowerInvariant(), cwd);
        using var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30));
        return File.Exists(path)
            ? JsonSerializer.Deserialize<DispatchBinding>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    public bool IsManaged(string provider, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Directory.Exists(root))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles(
                     root,
                     provider.Trim().ToLowerInvariant() + ".json",
                     SearchOption.AllDirectories))
        {
            using var gate = CrossProcessFileGate.Acquire(path + ".lock", TimeSpan.FromSeconds(30));
            DispatchBinding? binding;
            try
            {
                binding = JsonSerializer.Deserialize<DispatchBinding>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (binding?.ManagedByCccg == true
                && string.Equals(binding.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<Peer> Mark(IReadOnlyList<Peer> peers, string provider, string? cwd)
    {
        var binding = Load(provider, cwd);
        if (binding is null)
        {
            return peers;
        }

        return peers
            .Select(peer => peer with
            {
                Bound = string.Equals(peer.SessionId, binding.SessionId, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private string PathFor(string provider, string? cwd)
    {
        var key = string.IsNullOrWhiteSpace(cwd) ? "_" : cwd.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(root, hash, provider + ".json");
    }
}
