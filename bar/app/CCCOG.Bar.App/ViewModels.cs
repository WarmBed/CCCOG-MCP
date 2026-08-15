using System.Text.Json;

namespace CCCOG.Bar.App;

/// <summary>Provider hue only — status is communicated separately by
/// brightness/pulse so a provider's color never changes meaning.</summary>
internal static class ProviderPalette
{
    public static Windows.UI.Color Color(string provider) => provider.ToLowerInvariant() switch
    {
        "claude" => Windows.UI.Color.FromArgb(255, 0xDA, 0x77, 0x56),
        "codex" => Windows.UI.Color.FromArgb(255, 0x3B, 0x82, 0xF6),
        "grok" => Windows.UI.Color.FromArgb(255, 0x1F, 0x29, 0x37),
        _ => Windows.UI.Color.FromArgb(255, 0x8A, 0x94, 0xA6),
    };

    /// <summary>Same table as <see cref="Color"/>, as a hex string — the shape
    /// TokenBar's <c>Ui.Disc(string hex)</c> takes (ported verbatim in
    /// Ui.cs), used by the quota section's per-provider disc.</summary>
    public static string Hex(string provider) => provider.ToLowerInvariant() switch
    {
        "claude" => "#DA7756",
        "codex" => "#3B82F6",
        "grok" => "#1F2937",
        _ => "#8A94A6",
    };
}

/// <summary>Status maps to dot brightness (and pulse for active work) —
/// never to hue. Keeping this table in one place is what stops a future
/// edit from quietly overriding a provider's color for "failed" etc.</summary>
internal static class StatusVisual
{
    public static double Opacity(string status) => status.ToLowerInvariant() switch
    {
        "running" or "working" => 1.0,
        "queued" or "starting" => 0.72,
        "succeeded" or "live" or "owned" => 0.85,
        "failed" => 0.4,
        "stale" => 0.32,
        _ => 0.55,
    };

    public static bool Pulses(string status) =>
        status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("working", StringComparison.OrdinalIgnoreCase);
}

public sealed class GraphNodeViewModel
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string Label { get; init; }
    public required string State { get; init; }
}

public sealed class GraphEdgeViewModel
{
    public required string Id { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required string SourceLabel { get; init; }
    public required string TargetLabel { get; init; }
    public required string TargetProvider { get; init; }
    public string Kind { get; init; } = "dispatch";
    public string TaskSummary { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public int Count { get; init; } = 1;
    public bool IsActive { get; init; }
    /// <summary>An active job queued/running past the 6h stale threshold —
    /// never rendered as forever-"active"; it collapses into the stale
    /// group instead (lessons-learned #2).</summary>
    public bool IsStale { get; init; }
    public bool SourceIsUnknown { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<string> DetailSummaries { get; init; } = [];
}

/// <summary>A single flyout row. Built from a <see cref="GraphEdgeViewModel"/>
/// (or a synthesized stale-group aggregate) — never bound directly so the
/// view stays decoupled from the reader's internal shape.</summary>
public sealed class FlowRowViewModel
{
    public required string ChainText { get; init; }
    public string CountText { get; init; } = "";
    public required string ElapsedText { get; init; }
    /// <summary>The locked "elapsed | ×N" format, collapsing to just the
    /// elapsed text when there is nothing to aggregate.</summary>
    public string MetaText => string.IsNullOrEmpty(CountText) ? ElapsedText : $"{ElapsedText} | {CountText}";
    public required string TaskSummary { get; init; }
    public required Microsoft.UI.Xaml.Media.Brush DotBrush { get; init; }
    public double DotOpacity { get; init; } = 1.0;
    public bool Pulse { get; init; }
    public Microsoft.UI.Xaml.Media.Brush LabelBrush { get; init; } =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0xFA, 0xFC));
    public bool IsStaleGroup { get; init; }
}

public sealed class QuotaCardViewModel
{
    public required string ProviderId { get; init; }
    public required string Label { get; init; }
    /// <summary>The window's own label without the provider prefix (e.g.
    /// "5h limit") — what TokenBar's QuotaRow prints as the row title, since
    /// TokenBar groups rows under a provider header and would otherwise
    /// repeat the provider name on every row.</summary>
    public required string WindowLabel { get; init; }
    public double UsedPercent { get; init; }
    public string UsedText => $"{UsedPercent:0.#}% used";
    public string ResetText { get; init; } = "Reset time unavailable";
    /// <summary>Concrete failure reason (e.g. "HTTP 401", "token refresh
    /// rejected") — never a vague "unavailable" (locked UX requirement).</summary>
    public string StateText { get; init; } = "fresh";
    public bool IsFailure { get; init; }
    /// <summary>True only for the synthesized single-entry card a provider
    /// gets when it has no windows at all (auth failure before any quota
    /// window could be read) — rendered as TokenBar renders `agent.Error`:
    /// one dim line under the provider header, no gauge bar.</summary>
    public bool IsWholeProviderFailure { get; init; }
    public Microsoft.UI.Xaml.Media.Brush AccentBrush =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(ProviderPalette.Color(ProviderId));
}

internal sealed class LocalSnapshotReader
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(6);

    private readonly string _dispatchRoot;

    public LocalSnapshotReader()
    {
        _dispatchRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCCG", "dispatch");
    }

    public (IReadOnlyList<GraphNodeViewModel> Nodes, IReadOnlyList<FlowRowViewModel> Rows, int VisibleJobs) Read(
        DateTimeOffset? now = null)
    {
        var nodes = new Dictionary<string, GraphNodeViewModel>(StringComparer.Ordinal);
        var edges = new List<GraphEdgeViewModel>();
        var referenceTime = now ?? DateTimeOffset.Now;
        var jobsRoot = Path.Combine(_dispatchRoot, "jobs");
        if (Directory.Exists(jobsRoot))
        {
            foreach (var statusPath in Directory.EnumerateFiles(jobsRoot, "status.json", SearchOption.AllDirectories))
            {
                if (statusPath.Contains("cccg-archive", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    ReadJob(statusPath, nodes, edges, referenceTime);
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
        }

        var ownersRoot = Path.Combine(_dispatchRoot, "owners");
        if (Directory.Exists(ownersRoot))
        {
            foreach (var ownerPath in Directory.EnumerateFiles(ownersRoot, "*.json", SearchOption.AllDirectories))
            {
                if (ownerPath.Contains("cccg-archive", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(Path.ChangeExtension(ownerPath, ".lock")))
                {
                    continue;
                }
                try
                {
                    ReadOwner(ownerPath, nodes, edges);
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }

        var rows = BuildRows(edges);
        return (nodes.Values.ToArray(), rows, rows.Count);
    }

    private void ReadJob(
        string statusPath,
        Dictionary<string, GraphNodeViewModel> nodes,
        List<GraphEdgeViewModel> edges,
        DateTimeOffset referenceTime)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
        var root = document.RootElement;
        var jobId = String(root, "jobId") ?? Path.GetFileName(Path.GetDirectoryName(statusPath));
        var jobProvider = String(root, "provider") ?? "unknown";
        var sessionId = String(root, "sessionId") ?? String(root, "requestedSessionId") ?? "unknown";
        var target = $"{jobProvider}:{sessionId}";
        var status = String(root, "status") ?? "unknown";
        var updatedAt = ParseTime(root, "finishedAt") ??
                        ParseTime(root, "startedAt") ??
                        ParseTime(root, "createdAt") ??
                        File.GetLastWriteTimeUtc(statusPath);
        var active = IsActive(status);
        var stale = active && (referenceTime - updatedAt.ToLocalTime()) > StaleThreshold;

        nodes.TryAdd(target, new GraphNodeViewModel
        {
            Id = target, Provider = jobProvider, Label = sessionId, State = status,
        });

        var callerLabel = String(root, "callerLabel");
        var sourceIsUnknown = string.IsNullOrWhiteSpace(callerLabel);
        var sourceLabel = callerLabel ??
                          $"{String(root, "hopSource") ?? "unknown"}:{String(root, "hopChain") ?? "unknown"}";
        var source = $"caller:{sourceLabel}";
        nodes.TryAdd(source, new GraphNodeViewModel
        {
            Id = source, Provider = "caller", Label = sourceLabel, State = "active",
        });

        var promptPath = Path.Combine(Path.GetDirectoryName(statusPath) ?? "", "prompt.txt");
        var summary = ReadTaskSummary(promptPath);
        edges.Add(new GraphEdgeViewModel
        {
            Id = $"job:{jobId}",
            SourceNodeId = source,
            TargetNodeId = target,
            SourceLabel = Shorten(sourceLabel, 20),
            TargetLabel = Shorten(sessionId, 24),
            TargetProvider = jobProvider,
            Kind = "dispatch",
            TaskSummary = summary,
            Status = status,
            Count = 1,
            IsActive = active,
            IsStale = stale,
            SourceIsUnknown = sourceIsUnknown,
            UpdatedAt = updatedAt,
            DetailSummaries = summary.Length == 0 ? [] : [summary],
        });
    }

    private static void ReadOwner(
        string ownerPath,
        Dictionary<string, GraphNodeViewModel> nodes,
        List<GraphEdgeViewModel> edges)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ownerPath));
        var root = document.RootElement;
        var ownerProvider = String(root, "provider") ?? "unknown";
        var sessionId = String(root, "sessionId");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
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
            Id = ownerId,
            SourceNodeId = ownerId,
            TargetNodeId = target,
            SourceLabel = Shorten(ownerId[6..], 20),
            TargetLabel = Shorten(sessionId, 24),
            TargetProvider = ownerProvider,
            Kind = "path_a_owner",
            TaskSummary = "PATH A owner",
            Status = "live",
            IsActive = true,
            DetailSummaries = ["PATH A owner"],
        });
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

    private static string Shorten(string value, int length)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return clean.Length <= length ? clean : clean[..Math.Max(1, length - 1)] + "…";
    }

    private static DateTimeOffset? ParseTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool IsActive(string status) => status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                                                   status.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                                                   status.Equals("starting", StringComparison.OrdinalIgnoreCase) ||
                                                   status.Equals("working", StringComparison.OrdinalIgnoreCase);

    private static string FormatElapsed(DateTimeOffset? updatedAt, bool active)
    {
        if (updatedAt is null)
        {
            return active ? "active" : "";
        }
        var age = DateTimeOffset.Now - updatedAt.Value.ToLocalTime();
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        var elapsed = age < TimeSpan.FromMinutes(1) ? "<1m" :
                      age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes}m" :
                      $"{(int)age.TotalHours}h{(int)age.TotalMinutes % 60:00}m";
        return active ? elapsed : $"{elapsed} ago";
    }

    /// <summary>
    /// Active rows first (most recently updated first), then a single
    /// collapsed "stale ×N" group for active jobs stuck &gt; 6h, then
    /// aggregated terminal rows, then live PATH A owner rows. This is the
    /// locked ordering — never a flat alphabetical re-sort, which would
    /// interleave active and finished work.
    /// </summary>
    private static IReadOnlyList<FlowRowViewModel> BuildRows(IReadOnlyList<GraphEdgeViewModel> edges)
    {
        var dispatch = edges.Where(edge => edge.Kind == "dispatch").ToList();
        var owners = edges.Where(edge => edge.Kind != "dispatch").ToList();

        var activeFresh = dispatch.Where(edge => edge.IsActive && !edge.IsStale)
            .OrderByDescending(edge => edge.UpdatedAt)
            .ThenBy(edge => edge.SourceNodeId, StringComparer.Ordinal)
            .ToList();
        var activeStale = dispatch.Where(edge => edge.IsActive && edge.IsStale).ToList();
        var terminal = dispatch.Where(edge => !edge.IsActive).ToList();

        var rows = new List<FlowRowViewModel>();
        foreach (var edge in activeFresh)
        {
            rows.Add(ToRow(edge, count: 1));
        }

        if (activeStale.Count > 0)
        {
            rows.Add(new FlowRowViewModel
            {
                ChainText = $"stale ×{activeStale.Count}",
                ElapsedText = "> 6h queued",
                TaskSummary = string.Join(
                    Environment.NewLine,
                    activeStale.SelectMany(edge => edge.DetailSummaries).Take(4)),
                DotBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 0x6B, 0x72, 0x80)),
                DotOpacity = StatusVisual.Opacity("stale"),
                Pulse = false,
                IsStaleGroup = true,
            });
        }

        foreach (var group in terminal.GroupBy(edge => (edge.SourceNodeId, edge.TargetNodeId))
                     .OrderBy(group => group.Key.SourceNodeId, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.TargetNodeId, StringComparer.Ordinal))
        {
            var items = group.OrderByDescending(edge => edge.UpdatedAt).ToList();
            rows.Add(ToRow(items[0], count: items.Count));
        }

        foreach (var owner in owners.OrderBy(edge => edge.SourceNodeId, StringComparer.Ordinal))
        {
            rows.Add(ToRow(owner, count: 1));
        }

        return rows;
    }

    private static FlowRowViewModel ToRow(GraphEdgeViewModel edge, int count)
    {
        var labelBrush = edge.SourceIsUnknown
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(180, 0x9A, 0xA5, 0xB4))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0xFA, 0xFC));
        return new FlowRowViewModel
        {
            ChainText = $"{edge.SourceLabel} → {edge.TargetLabel}",
            CountText = count > 1 ? $"×{count}" : "",
            ElapsedText = FormatElapsed(edge.UpdatedAt, edge.IsActive),
            TaskSummary = edge.DetailSummaries.Count == 0
                ? "No task summary"
                : string.Join(Environment.NewLine, edge.DetailSummaries.Take(4)),
            DotBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(ProviderPalette.Color(edge.TargetProvider)),
            DotOpacity = StatusVisual.Opacity(edge.Status),
            Pulse = StatusVisual.Pulses(edge.Status),
            LabelBrush = labelBrush,
        };
    }
}
