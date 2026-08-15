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

/// <summary>A single flyout row, built from one windowed/aggregated row of
/// <c>cccog_bar_control_snapshot</c>'s payload — see
/// <see cref="NativeControlClient"/> in FlyoutWindow.xaml.cs. Never bound
/// directly to the wire JSON so the view stays decoupled from its shape.</summary>
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

// LocalSnapshotReader (the from-scratch, unwindowed dispatch-root scan) was
// removed here: it dumped every terminal job ever recorded, unbounded by
// age, which is what produced the "33 raw rows with naked session-UUID
// targets" regression. Flow rows now come from
// NativeControlClient.TryFetch() (FlyoutWindow.xaml.cs), which calls the
// new `cccog_bar_control_snapshot` FFI entry — windowing, `×N` aggregation,
// and target-title resolution now live in Rust
// (`cccog_bar_core::control`), not here. See bar/SYNC.md.
