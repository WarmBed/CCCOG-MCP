using System.Collections.ObjectModel;
using System.Text.Json;

namespace CCCOG.Bar.App;

public sealed class GraphNodeViewModel
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string Label { get; init; }
    public required string State { get; init; }
    public string TokenText { get; init; } = "0 tokens";
}

public sealed class GraphEdgeViewModel
{
    public required string Id { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required string Kind { get; init; }
    public string TaskSummary { get; init; } = "";
    public string Status { get; init; } = "unknown";
}

public sealed class QuotaCardViewModel
{
    public required string Label { get; init; }
    public double UsedPercent { get; init; }
    public string UsedText => $"{UsedPercent:0.#}% used";
    public string ResetText { get; init; } = "Reset time unavailable";
    public string StateText { get; init; } = "fresh";
}

internal sealed class LocalSnapshotReader
{
    private readonly string _dispatchRoot;

    public LocalSnapshotReader()
    {
        _dispatchRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG", "dispatch");
    }

    public (IReadOnlyList<GraphNodeViewModel> Nodes, IReadOnlyList<GraphEdgeViewModel> Edges, int Jobs) Read(string provider)
    {
        var nodes = new Dictionary<string, GraphNodeViewModel>(StringComparer.Ordinal);
        var edges = new List<GraphEdgeViewModel>();
        var jobsRoot = Path.Combine(_dispatchRoot, "jobs");
        if (!Directory.Exists(jobsRoot))
        {
            return ([], [], 0);
        }

        foreach (var statusPath in Directory.EnumerateFiles(jobsRoot, "status.json", SearchOption.AllDirectories))
        {
            if (statusPath.Contains("cccg-archive", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
                var root = document.RootElement;
                var jobId = String(root, "jobId") ?? Path.GetFileName(Path.GetDirectoryName(statusPath));
                var jobProvider = String(root, "provider") ?? "unknown";
                if (provider != "all" && !jobProvider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var sessionId = String(root, "sessionId") ?? String(root, "requestedSessionId") ?? "unknown";
                var target = $"{jobProvider}:{sessionId}";
                var status = String(root, "status") ?? "unknown";
                if (!nodes.ContainsKey(target))
                {
                    nodes[target] = new GraphNodeViewModel
                    {
                        Id = target, Provider = jobProvider, Label = sessionId, State = status,
                    };
                }
                var sourceLabel = String(root, "callerLabel") ??
                                  $"{String(root, "hopSource") ?? "unknown"}:{String(root, "hopChain") ?? "unknown"}";
                var source = $"caller:{sourceLabel}";
                if (!nodes.ContainsKey(source))
                {
                    nodes[source] = new GraphNodeViewModel
                    {
                        Id = source, Provider = "caller", Label = sourceLabel, State = "active",
                    };
                }
                var promptPath = Path.Combine(Path.GetDirectoryName(statusPath) ?? "", "prompt.txt");
                var summary = ReadTaskSummary(promptPath);
                edges.Add(new GraphEdgeViewModel
                {
                    Id = $"job:{jobId}", SourceNodeId = source, TargetNodeId = target,
                    Kind = "dispatch", TaskSummary = summary,
                    Status = status,
                });
            }
            catch (IOException)
            {
                // A provider may be atomically replacing a status file; skip
                // this pass and let the next refresh publish a complete view.
            }
            catch (JsonException)
            {
                // Partial JSON is data, never an instruction or a UI crash.
            }
        }

        var ownersRoot = Path.Combine(_dispatchRoot, "owners");
        if (Directory.Exists(ownersRoot))
        {
            foreach (var ownerPath in Directory.EnumerateFiles(ownersRoot, "*.json", SearchOption.AllDirectories))
            {
                if (ownerPath.Contains("cccg-archive", StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.ChangeExtension(ownerPath, ".lock")) is false)
                {
                    continue;
                }
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(ownerPath));
                    var root = document.RootElement;
                    var ownerProvider = String(root, "provider") ?? "unknown";
                    if (provider != "all" && !ownerProvider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var sessionId = String(root, "sessionId");
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        continue;
                    }
                    var ownerId = $"owner:{Path.GetFileNameWithoutExtension(ownerPath)}";
                    var target = $"{ownerProvider}:{sessionId}";
                    nodes.TryAdd(ownerId, new GraphNodeViewModel
                    {
                        Id = ownerId, Provider = "owner", Label = ownerId[6..], State = "active",
                    });
                    nodes.TryAdd(target, new GraphNodeViewModel
                    {
                        Id = target, Provider = ownerProvider, Label = sessionId, State = "owned",
                    });
                    edges.Add(new GraphEdgeViewModel
                    {
                        Id = ownerId, SourceNodeId = ownerId, TargetNodeId = target,
                        Kind = "path_a_owner", TaskSummary = "PATH A owner", Status = "live",
                    });
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }
        return (nodes.Values.ToArray(), edges, edges.Count);
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadTaskSummary(string path)
    {
        if (!File.Exists(path))
        {
            return "";
        }
        try
        {
            var lines = File.ReadLines(path).Take(4).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            var task = lines.FirstOrDefault(line => !line.StartsWith("[CCCG dispatch from ", StringComparison.Ordinal)) ?? "";
            var safe = new string(task.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            return safe.Length > 100 ? safe[..100] + "…" : safe;
        }
        catch (IOException)
        {
            return "";
        }
    }
}
