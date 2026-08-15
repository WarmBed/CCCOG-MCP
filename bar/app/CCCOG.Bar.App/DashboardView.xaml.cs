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

    public void RenderQuota(IReadOnlyList<QuotaCardViewModel> cards, bool everHadQuota)
    {
        QuotaHost.Content = Ui.Card("Quota", BuildQuotaContent(cards, everHadQuota));
    }

    public void RenderFlow(IReadOnlyList<FlowRowViewModel> rows, int visibleJobs)
    {
        FlowHost.Content = Ui.Card("Flow", BuildFlowContent(rows));
        FooterText.Text = $"{visibleJobs} flow row(s) - tokens only";
    }

    // ── Quota section: TokenBar Agent-limits-card visuals ──────────────────
    // (DashboardView.xaml.cs: BuildLimits/QuotaRow/GaugeBar), fed by
    // CCCOG's QuotaCardViewModel instead of TokenBar's UsageWindow/
    // UsagePaceRowPresentation.

    private static FrameworkElement BuildQuotaContent(
        IReadOnlyList<QuotaCardViewModel> cards, bool everHadQuota)
    {
        if (cards.Count == 0)
        {
            return Ui.Dim(everHadQuota ? "No quota data yet." : "Loading quota…");
        }

        var panel = new StackPanel { Spacing = 10 };
        foreach (var group in cards.GroupBy(card => card.ProviderId))
        {
            var section = new StackPanel { Spacing = 5 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(Ui.Disc(ProviderPalette.Hex(group.Key)));
            header.Children.Add(Ui.Text(group.Key, 12, bold: true));
            section.Children.Add(header);

            foreach (var card in group)
            {
                section.Children.Add(QuotaRow(card));
            }

            panel.Children.Add(section);
        }

        return panel;
    }

    /// <summary>One quota window row: label + reset time header, gauge bar,
    /// used-percent footer — TokenBar's "classic" QuotaRow layout (the shape
    /// TokenBar itself falls back to when there is no pace/projection data
    /// to show, which is always true for CCCOG's DTO). A whole-provider
    /// failure (no windows ever read) renders as TokenBar renders
    /// `agent.Error`: the header row plus one dim reason line, no bar. A
    /// provider-stale-but-has-a-value window (windows exist, top-level state
    /// isn't "fresh") still gets the full bar/percent — the diagnostic is
    /// appended as an extra dim line below, matching the previous shell's
    /// "always show a concrete reason alongside real numbers" behavior.</summary>
    private static FrameworkElement QuotaRow(QuotaCardViewModel card)
    {
        var root = new StackPanel { Spacing = 3 };
        root.Children.Add(Ui.Row(
            Ui.Text(card.WindowLabel, 11, bold: true),
            Ui.Text(card.IsWholeProviderFailure ? "" : card.ResetText, 10, 0.6)));

        if (card.IsWholeProviderFailure)
        {
            root.Children.Add(Ui.Dim(card.StateText, 11));
            return root;
        }

        root.Children.Add(Ui.GaugeBar(card.UsedPercent));
        root.Children.Add(Ui.Text(card.UsedText, 10, 0.75));
        if (card.IsFailure)
        {
            root.Children.Add(Ui.Dim(card.StateText, 11));
        }

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
