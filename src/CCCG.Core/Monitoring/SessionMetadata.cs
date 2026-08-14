using System.Text.Json;

namespace CCCG.Core.Monitoring;

public sealed record SessionMetadataSnapshot(
    string RawSessionId,
    string? RawCliSessionId,
    long LastActivityAt,
    int CompletedTurns,
    string? Model,
    string? Effort,
    string? PermissionMode,
    IReadOnlyList<string> RawBridgeSessionIds,
    long FileLength,
    DateTime LastWriteTimeUtc);

public static class SessionMetadataReader
{
    public static SessionMetadataSnapshot? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var sessionId = ReadString(root, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var bridges = new List<string>();
            if (root.TryGetProperty("bridgeSessionIds", out var bridgeElement)
                && bridgeElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var bridge in bridgeElement.EnumerateArray())
                {
                    if (bridge.ValueKind == JsonValueKind.String
                        && bridge.GetString() is { Length: > 0 } bridgeId)
                    {
                        bridges.Add(bridgeId);
                    }
                }
            }

            var file = new FileInfo(path);
            return new SessionMetadataSnapshot(
                sessionId,
                ReadString(root, "cliSessionId"),
                ReadInt64(root, "lastActivityAt"),
                ReadInt32(root, "completedTurns"),
                ReadString(root, "model"),
                ReadString(root, "effort"),
                ReadString(root, "permissionMode"),
                bridges,
                file.Length,
                file.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static int ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
}
