using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCCG.Core.Dispatch;

/// <summary>
/// One CCCG-owned provider session: a daemon that spawned the provider child
/// with stdin kept open and accepts spooled messages as new turns.
/// </summary>
public sealed record OwnerRegistration(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("ownerPid")] int OwnerPid,
    [property: JsonPropertyName("spoolDir")] string SpoolDir,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

/// <summary>
/// A live registration together with the registry key it is filed under. The
/// key names the lease file, so callers can re-check liveness
/// (<see cref="OwnerRegistry.LeaseIsHeld"/>) without another directory scan —
/// and without being fooled by PID reuse.
/// </summary>
public sealed record OwnerRegistryEntry(string Key, OwnerRegistration Registration);

/// <summary>
/// File registry of live owner daemons under
/// %LOCALAPPDATA%\CCCG\dispatch\owners. An entry counts only while its
/// DeleteOnClose lease is still held; a dead owner leaves an openable (or
/// absent) lease and is treated as stale.
/// </summary>
public sealed class OwnerRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string root;

    public OwnerRegistry(string? root = null)
    {
        this.root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "dispatch",
            "owners");
    }

    public string Root => root;

    public static string Key(string provider, string? cwd, string? sessionId)
    {
        var normalizedCwd = string.IsNullOrWhiteSpace(cwd)
            ? "_"
            : Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var normalizedSession = string.IsNullOrWhiteSpace(sessionId)
            ? "_"
            : sessionId.Trim().ToLowerInvariant();
        var value = provider.Trim().ToLowerInvariant() + "|" + normalizedCwd + "|" + normalizedSession;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string RegistrationPath(string key) => Path.Combine(root, key + ".json");

    public string LeasePath(string key) => Path.Combine(root, key + ".lock");

    public string SpoolDirectory(string key) => Path.Combine(root, key, "spool");

    /// <summary>
    /// Exclusive DeleteOnClose ownership lease. Process death releases it, so
    /// lease-held is the liveness signal for the registration.
    /// </summary>
    public FileStream AcquireLease(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Directory.CreateDirectory(root);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(
                    LeasePath(key),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException exception)
            {
                // A concurrent LeaseIsHeld probe holds a short-lived read
                // handle; one retry rides out that transient conflict without
                // letting a legitimate acquisition fail.
                if (attempt == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }

                throw new InvalidOperationException(
                    "Another CCCG owner daemon already owns this provider session.",
                    exception);
            }
        }
    }

    public void Register(string key, OwnerRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(registration);
        CrossProcessFileGate.AtomicWriteAllText(
            RegistrationPath(key),
            JsonSerializer.Serialize(registration, JsonOptions));
    }

    public void Unregister(string key)
    {
        try
        {
            File.Delete(RegistrationPath(key));
        }
        catch (IOException)
        {
        }
    }

    public bool LeaseIsHeld(string key)
    {
        var leasePath = LeasePath(key);
        if (!File.Exists(leasePath))
        {
            return false;
        }

        try
        {
            // Least-intrusive probe: read access with a permissive share so a
            // concurrent legitimate AcquireLease is disturbed as little as
            // possible. A held lease (FileShare.None) still rejects this open.
            using var stream = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException)
        {
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

    /// <summary>
    /// Finds the live owner for a provider session. Matches by session id when
    /// one is given, otherwise by workspace. Registrations whose lease is no
    /// longer held are stale and never returned.
    /// </summary>
    public OwnerRegistration? TryFind(string provider, string? sessionId, string? cwd) =>
        TryFindEntry(provider, sessionId, cwd)?.Registration;

    /// <summary>
    /// Like <see cref="TryFind"/> but also returns the registry key so callers
    /// can keep probing the same lease during long waits.
    /// </summary>
    public OwnerRegistryEntry? TryFindEntry(string provider, string? sessionId, string? cwd)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        provider = provider.Trim().ToLowerInvariant();
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            OwnerRegistration? registration;
            try
            {
                registration = JsonSerializer.Deserialize<OwnerRegistration>(
                    File.ReadAllText(path));
            }
            catch (JsonException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            if (registration is not { SchemaVersion: 1 }
                || !string.Equals(registration.Provider, provider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matches = !string.IsNullOrWhiteSpace(sessionId)
                ? string.Equals(registration.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                : PathsMatch(registration.Cwd, cwd);
            if (!matches)
            {
                continue;
            }

            var key = Path.GetFileNameWithoutExtension(path);
            if (!LeaseIsHeld(key))
            {
                continue;
            }

            return new OwnerRegistryEntry(key, registration);
        }

        return null;
    }

    private static bool PathsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
