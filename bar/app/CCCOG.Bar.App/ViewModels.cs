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
/// edit from quietly overriding a provider's color for "failed" etc. The
/// tree's own three-word vocabulary (執行中/閒置/resumable, plus the
/// "terminal" bottom-aggregate bucket — see <c>cccog_bar_core::tree</c>)
/// lives in the same table so a Flow row's brightness always comes from one
/// place regardless of which of the two Flow data sources it came from.</summary>
internal static class StatusVisual
{
    public static double Opacity(string status) => status.ToLowerInvariant() switch
    {
        "running" or "working" => 1.0,
        "queued" or "starting" => 0.72,
        "succeeded" or "live" or "owned" => 0.85,
        "failed" => 0.4,
        "stale" => 0.32,
        "執行中" => 1.0,
        "閒置" => 0.75,
        "resumable" => 0.4,
        "terminal" => 0.32,
        _ => 0.55,
    };

    public static bool Pulses(string status) =>
        status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("working", StringComparison.OrdinalIgnoreCase) ||
        status == "執行中";
}

/// <summary>One row of the Flow tree (<c>cccog_bar_core::tree::TreeRow</c>,
/// via <c>cccog_bar_control_snapshot</c>'s new <c>tree</c> field — see
/// <see cref="NativeControlClient"/> in FlyoutWindow.xaml.cs). The tree is
/// exactly two levels deep and is sent already flattened with a
/// <see cref="Depth"/> tag rather than a nested DTO — the shell renders
/// indentation from <see cref="Depth"/> instead of hosting a real recursive
/// tree control (see bar/SYNC.md for why).</summary>
public sealed class FlowTreeRowViewModel
{
    public required int Depth { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    /// <summary>"agent · sonnet-5" etc — present on child rows (Depth 1),
    /// null on top-level rows.</summary>
    public string? TypeModelText { get; init; }
    /// <summary>Drives both the dot color and — for a child row only — the
    /// <see cref="TypeModelText"/> run's own color (operator rule: "same
    /// palette as the dots"). Task 12 (2026-08-17): this row is now built on
    /// a background thread (<c>FlyoutWindow.NativeControlClient.ToRow</c>,
    /// off the UI thread per the async-fetch fix) — a `SolidColorBrush` used
    /// to be constructed right there and stored as a `DotBrush` property,
    /// which threw a `COMException` every single time (WinUI XAML objects
    /// have UI-thread/apartment affinity; constructing one off-thread fails
    /// silently into an empty Flow tree, since the exception unwound out of
    /// the whole fetch). `Provider` — a plain string, no thread affinity —
    /// is carried instead, and `DashboardView.BuildFlowRow` (which DOES run
    /// on the UI thread, after the DispatcherQueue hop) builds the actual
    /// `SolidColorBrush` from it at render time, exactly like it already did
    /// for the type/model text's own color a few lines below.</summary>
    public required string Provider { get; init; }
    public required string StateLabel { get; init; }
    public bool Pulse { get; init; }
    public required string ElapsedText { get; init; }
    public double DotOpacity { get; init; } = 1.0;
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
    /// <summary>No "used" suffix (operator direction, bar/SYNC.md).</summary>
    public string UsedText => $"{UsedPercent:0.#}%";
    public string ResetText { get; init; } = "Reset time unavailable";
    /// <summary>The single composed row text — "&lt;abbreviated
    /// window&gt;  &lt;NN%&gt;  &lt;date&gt;" (e.g. "5h  7%  08/20 11:50") —
    /// assembled once in Rust (cccog_bar_quota::format_window_line) so this
    /// view stays display-only; see bar/SYNC.md. Falls back to composing
    /// the same shape locally only if an older FFI build omitted the field
    /// (defense against a stale DLL, not the normal path).</summary>
    public string DisplayLine { get; init; } = "";
    /// <summary>Concrete failure reason (e.g. "HTTP 401", "token refresh
    /// rejected") — never a vague "unavailable" (locked UX requirement).
    /// Condensed to one short line (operator direction, bar/SYNC.md): the
    /// fuller sentence this was trimmed from lives in
    /// <see cref="DiagnosticDetail"/> instead of being dropped.</summary>
    public string StateText { get; init; } = "fresh";
    /// <summary>The un-condensed diagnostic (dispatch-failure time, verbatim
    /// provider text) — shown as a hover tooltip on the card's status line
    /// so nothing <see cref="StateText"/> trimmed off is actually lost.
    /// `null` when there was nothing extra to trim (StateText already told
    /// the whole story).</summary>
    public string? DiagnosticDetail { get; init; }
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
