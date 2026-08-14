using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CCCG.Core.Providers;

public sealed record CodexThreadBinding(
    int SchemaVersion,
    string ThreadId,
    string BackendModel,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed class CodexThreadBindingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string root;

    public CodexThreadBindingStore(string? root = null)
    {
        this.root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG",
            "session-bindings",
            "v1");
    }

    public string GetBindingPath(string sessionKey) =>
        Path.Combine(root, Hash(sessionKey) + ".json");

    public CodexThreadBinding? Load(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var path = GetBindingPath(sessionKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var binding = JsonSerializer.Deserialize<CodexThreadBinding>(
                File.ReadAllText(path));
            return binding is { SchemaVersion: 1 }
                && !string.IsNullOrWhiteSpace(binding.ThreadId)
                && !string.IsNullOrWhiteSpace(binding.BackendModel)
                    ? binding
                    : throw new InvalidDataException(
                        "The CCCG session binding is incomplete or has an unsupported version.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The CCCG session binding is not valid JSON.",
                exception);
        }
    }

    public void Save(string sessionKey, CodexThreadBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentNullException.ThrowIfNull(binding);
        Directory.CreateDirectory(root);

        var path = GetBindingPath(sessionKey);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(binding, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A successful atomic move already removed the temporary file.
            }
        }
    }

    public FileStream AcquireLease(string sessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Hash(sessionKey) + ".lock");
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another CCCG router already owns this Desktop session binding.",
                exception);
        }
    }

    private static string Hash(string sessionKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
