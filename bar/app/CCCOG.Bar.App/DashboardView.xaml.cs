using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace CCCOG.Bar.App;

/// <summary>
/// The two-section content view (Quota, Flow) hosted inside
/// <see cref="FlyoutWindow"/>. The header/footer chrome and the section
/// container pattern are ported from TokenBar-Windows
/// <c>DashboardView.xaml.cs</c>; the section builders below
/// (<see cref="BuildQuotaContent"/>, <see cref="QuotaRow"/>,
/// <see cref="BuildFlowContent"/>) are adapted from TokenBar's
/// <c>BuildLimits</c>/<c>QuotaRow</c>/<c>GaugeBar</c> and row-rendering code
/// to CCCOG's own quota/flow DTOs — see bar/SYNC.md for the line-by-line
/// derivation record.
/// </summary>
public sealed partial class DashboardView : UserControl
{
    public event Action? RefreshRequested;

    public DashboardView()
    {
        InitializeComponent();
        RefreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        QuitButton.Click += (_, _) => App.QuitApp?.Invoke();
    }

    public void SetRefreshedText(string text) => RefreshedText.Text = text;

    /// <summary>TokenBar's manual-refresh spinner swap (one control toggling
    /// between the refresh glyph and a ProgressRing) — used here while a
    /// quota poll is in flight.</summary>
    public void ShowRefreshing()
    {
        RefreshButton.Visibility = Visibility.Collapsed;
        RefreshSpinner.Visibility = Visibility.Visible;
        RefreshSpinner.IsActive = true;
    }

    public void HideRefreshing()
    {
        RefreshSpinner.IsActive = false;
        RefreshSpinner.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Visible;
    }

    /// <summary>Three fixed-order, side-by-side cards (left/center/right =
    /// Claude/Codex/Grok — operator layout pass) instead of one grouped
    /// card. Each provider host renders independently off the same
    /// <paramref name="cards"/> list so a provider that hasn't reported yet
    /// still shows its own loading line (lessons-learned #1: never hide a
    /// section while loading) instead of the whole row disappearing.</summary>
    public void RenderQuota(IReadOnlyList<QuotaCardViewModel> cards, bool everHadQuota)
    {
        ClaudeQuotaHost.Content = BuildProviderQuotaCard("claude", "Claude", cards, everHadQuota);
        CodexQuotaHost.Content = BuildProviderQuotaCard("codex", "Codex", cards, everHadQuota);
        GrokQuotaHost.Content = BuildProviderQuotaCard("grok", "Grok", cards, everHadQuota);
    }

    public void RenderFlow(IReadOnlyList<FlowRowViewModel> rows, int visibleJobs)
    {
        FlowHost.Content = Ui.Card("Flow", BuildFlowContent(rows));
        FooterText.Text = $"{visibleJobs} flow row(s) - tokens only";
    }

    /// <summary>Route the flyout's WH_MOUSE_LL wheel event onto the Flow
    /// section (FlyoutWindow.xaml.cs's InstallWheelHook) — CCCOG has no 3D
    /// contribution graph to hit-test first, unlike TokenBar's own
    /// RouteGlobalWheel (TryZoomGraphAt), so this is straight to
    /// ScrollBy.</summary>
    public void RouteGlobalWheel(double windowPixelX, double windowPixelY, int delta) =>
        ScrollBy(-delta);

    public void ScrollBy(double delta) =>
        CardsScroll.ChangeView(null, CardsScroll.VerticalOffset + delta, null, disableAnimation: false);

    // ── Quota section: TokenBar Agent-limits-card visuals ──────────────────
    // (DashboardView.xaml.cs: BuildLimits/QuotaRow/GaugeBar), fed by
    // CCCOG's QuotaCardViewModel instead of TokenBar's UsageWindow/
    // UsagePaceRowPresentation.

    /// <summary>One provider's slot: a Ui.Card titled with the provider's
    /// display name (replacing the old grouped card's inline disc+name
    /// header — the card title itself carries that now) holding one
    /// QuotaRow per window. Loading and no-data-yet states render inside
    /// the card, never an empty/missing slot.</summary>
    private static FrameworkElement BuildProviderQuotaCard(
        string providerId, string displayName,
        IReadOnlyList<QuotaCardViewModel> cards, bool everHadQuota)
    {
        var providerCards = cards
            .Where(card => card.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        FrameworkElement content = providerCards.Count == 0
            ? Ui.Dim(everHadQuota ? "No data" : "Loading…")
            : BuildProviderRows(providerCards);
        return Ui.Card(displayName, content);
    }

    /// <summary>All of one provider's window rows, plus AT MOST ONE status
    /// line for the whole card (operator direction, bar/SYNC.md) — every
    /// window in a card shares the same card-level state/diagnostic, so
    /// printing it per window (the previous shape) just repeated the same
    /// line under every row and made cards taller than they needed to be.
    /// The status line's ToolTip carries <see cref="QuotaCardViewModel.DiagnosticDetail"/>
    /// — the fuller sentence StateText was condensed from, never actually
    /// discarded.</summary>
    private static FrameworkElement BuildProviderRows(IReadOnlyList<QuotaCardViewModel> cards)
    {
        var panel = new StackPanel { Spacing = 5 };
        foreach (var card in cards)
        {
            panel.Children.Add(QuotaRow(card));
        }

        // Exclude IsWholeProviderFailure: QuotaRow already renders that
        // card's own status line inline (its only "row" IS the status
        // line) — counting it again here would duplicate it.
        var failing = cards.FirstOrDefault(card => card.IsFailure && !card.IsWholeProviderFailure);
        if (failing is not null)
        {
            var status = Ui.Dim(failing.StateText, 11);
            if (!string.IsNullOrEmpty(failing.DiagnosticDetail))
            {
                ToolTipService.SetToolTip(status, failing.DiagnosticDetail);
            }
            panel.Children.Add(status);
        }

        return panel;
    }

    /// <summary>One quota window row, operator layout pass (bar/SYNC.md,
    /// final revision): a single composed line — "&lt;abbreviated
    /// window&gt;  &lt;NN%&gt;  &lt;date&gt;" (e.g. "5h  7%  08/20 11:50",
    /// no "used"/"resets" words) — assembled once in Rust
    /// (cccog_bar_quota::format_window_line, card.DisplayLine) so this view
    /// is display-only, with the window's gauge bar beneath it (TokenBar's
    /// agent-card row pattern). No per-window status line — a failing
    /// card's reason renders once, after every row (see
    /// <see cref="BuildProviderRows"/>), not repeated under each one.</summary>
    private static FrameworkElement QuotaRow(QuotaCardViewModel card)
    {
        var root = new StackPanel { Spacing = 3 };

        if (card.IsWholeProviderFailure)
        {
            // No separate WindowLabel header row (operator direction,
            // bar/SYNC.md): it only ever repeated the provider id the
            // card's own title already shows ("grok" under "Grok"). One
            // condensed status line is the whole card.
            var status = Ui.Dim(card.StateText, 11);
            if (!string.IsNullOrEmpty(card.DiagnosticDetail))
            {
                ToolTipService.SetToolTip(status, card.DiagnosticDetail);
            }
            root.Children.Add(status);
            return root;
        }

        root.Children.Add(Ui.Text(card.DisplayLine, 11, bold: true));
        root.Children.Add(Ui.GaugeBar(card.UsedPercent));
        return root;
    }

    // ── Flow section: TokenBar row primitives (status disc, text,
    // right-aligned meta) — Ui.Row()/Ui.Text(), fed by FlowRowViewModel.
    // Behavior (chains, ×N, stale group, tooltips) is unchanged from the
    // previous ItemsControl/DataTemplate shell.

    private static FrameworkElement BuildFlowContent(IReadOnlyList<FlowRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return Ui.Dim("No active or recent jobs");
        }

        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
        {
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            // Not Ui.Disc(hex): a flow dot's brush/opacity are dynamic
            // (status-driven, already resolved to a Brush by
            // LocalSnapshotReader), where Ui.Disc only takes a static hex
            // string — this is the one primitive that needed adapting
            // rather than reuse as-is.
            left.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = row.DotBrush,
                Opacity = row.DotOpacity,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var chainText = Ui.Text(row.ChainText, 11);
            chainText.Foreground = row.LabelBrush;
            left.Children.Add(chainText);

            var rowGrid = Ui.Row(left, Ui.Text(row.MetaText, 10, 0.62));
            ToolTipService.SetToolTip(rowGrid, row.TaskSummary);
            panel.Children.Add(rowGrid);
        }

        return panel;
    }
}
