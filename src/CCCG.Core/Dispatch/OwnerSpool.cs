using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCCG.Core.Dispatch;

public sealed record OwnerMessage(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("fromRole")] string FromRole,
    [property: JsonPropertyName("fromSessionId")] string? FromSessionId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public static class OwnerReceiptStatus
{
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

public sealed record OwnerReceipt(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("deliveredAt")] DateTimeOffset? DeliveredAt,
    [property: JsonPropertyName("responseText")] string? ResponseText,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// Per-owner delivery queue. Senders post messages into
/// <c>&lt;spoolDir&gt;\incoming</c>; the owner daemon runs each as a provider
/// turn and writes the outcome into <c>&lt;spoolDir&gt;\receipts</c>. A
/// receipt exists only after the provider turn finished, so "receipt with
/// status delivered" is the true delivery signal.
/// </summary>
public sealed class OwnerSpool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string spoolDir;

    public OwnerSpool(string spoolDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDir);
        this.spoolDir = spoolDir;
    }

    public string Root => spoolDir;

    public string IncomingDirectory => Path.Combine(spoolDir, "incoming");

    public string ReceiptsDirectory => Path.Combine(spoolDir, "receipts");

    public string Post(OwnerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageId);
        var path = Path.Combine(
            IncomingDirectory,
            message.CreatedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfff")
                + "_" + Sanitize(message.MessageId) + ".json");
        CrossProcessFileGate.AtomicWriteAllText(
            path,
            JsonSerializer.Serialize(message, JsonOptions));
        return path;
    }

    public IReadOnlyList<(string Path, OwnerMessage Message)> ReadIncoming()
    {
        if (!Directory.Exists(IncomingDirectory))
        {
            return Array.Empty<(string, OwnerMessage)>();
        }

        var items = new List<(string, OwnerMessage)>();
        foreach (var path in Directory.EnumerateFiles(IncomingDirectory, "*.json")
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            try
            {
                var message = JsonSerializer.Deserialize<OwnerMessage>(File.ReadAllText(path));
                if (message is not null && !string.IsNullOrWhiteSpace(message.MessageId))
                {
                    items.Add((path, message));
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
                // A concurrent atomic move can race the enumeration; the file
                // will be picked up on the next poll.
            }
        }

        return items;
    }

    public void Remove(string incomingPath)
    {
        try
        {
            File.Delete(incomingPath);
        }
        catch (IOException)
        {
        }
    }

    public void WriteReceipt(OwnerReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.MessageId);
        CrossProcessFileGate.AtomicWriteAllText(
            ReceiptPath(receipt.MessageId),
            JsonSerializer.Serialize(receipt, JsonOptions));
    }

    public OwnerReceipt? TryReadReceipt(string messageId)
    {
        var path = ReceiptPath(messageId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OwnerReceipt>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string ReceiptPath(string messageId) =>
        Path.Combine(ReceiptsDirectory, Sanitize(messageId) + ".json");

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return builder.ToString();
    }
}
