using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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

    public void RenderFlow(IReadOnlyList<FlowTreeRowViewModel> rows, int visibleRows)
    {
        FlowHost.Content = Ui.Card("Flow", BuildFlowContent(rows));
        FooterText.Text = $"{visibleRows} flow row(s) - tokens only";
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

        // Operator styling pass: the window/percent cluster ("5h 11%")
        // keeps its bold left position; the reset date/time moves to the
        // right edge of the card, non-bold, smaller, dim-secondary (Ui.Dim,
        // the same treatment TokenBar uses for its own timestamp text) —
        // C#-side layout only, `card.DisplayLine` (the single Rust-composed
        // string) is no longer used here, but the field itself stays for
        // the tooltip/back-compat callers already depending on it.
        var mainText = Ui.Text($"{card.WindowLabel}  {card.UsedText}", 11, bold: true);
        var timeText = Ui.Dim(card.ResetText, 9);
        root.Children.Add(Ui.Row(mainText, timeText));
        root.Children.Add(Ui.GaugeBar(card.UsedPercent));
        return root;
    }

    // ── Flow section: TokenBar row primitives (status disc, text,
    // right-aligned meta) — Ui.Row()/Ui.Text(), fed by the flattened,
    // depth-tagged Flow tree (FlowTreeRowViewModel — one controller session
    // per top-level row, its agent/session/codex/grok children indented
    // beneath). Two-level tree, rendered from a flat list rather than a
    // real recursive tree control: depth 0 is a controller/resumable row,
    // depth 1 is always its immediately-preceding depth-0 row's child (the
    // Rust side already groups children under their controller in order —
    // see cccog_bar_core::tree::build_tree), so indentation is just "extra
    // left margin when Depth == 1", no parent/child object graph needed on
    // this side. See bar/SYNC.md for the fuller derivation record.

    private static FrameworkElement BuildFlowContent(IReadOnlyList<FlowTreeRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return Ui.Dim("No live sessions or recent jobs");
        }

        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
        {
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = row.Depth > 0 ? new Thickness(18, 0, 0, 0) : new Thickness(0),
            };
            left.Children.Add(new Ellipse
            {
                Width = row.Depth > 0 ? 6 : 8,
                Height = row.Depth > 0 ? 6 : 8,
                Fill = row.DotBrush,
                Opacity = row.DotOpacity,
                VerticalAlignment = VerticalAlignment.Center,
            });
            left.Children.Add(FlowLabelText(row));

            var stateElapsed = string.IsNullOrEmpty(row.ElapsedText)
                ? row.StateLabel
                : $"{row.StateLabel} · {row.ElapsedText}";
            var rowGrid = Ui.Row(left, Ui.Text(stateElapsed, 10, 0.62));
            panel.Children.Add(rowGrid);
        }

        return panel;
    }

    /// <summary>A child row's label carries its own "(type · model)"
    /// parenthetical, colored with that child's own provider color
    /// (operator rule 2: "same palette as the dots") — everything else in
    /// the line stays the default label color. Built as two
    /// <see cref="Run"/>s inside one TextBlock rather than two separate
    /// TextBlocks so the colored segment visually reads as part of the same
    /// name, not a second column.</summary>
    private static TextBlock FlowLabelText(FlowTreeRowViewModel row)
    {
        var text = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run { Text = row.Label });
        if (!string.IsNullOrEmpty(row.TypeModelText))
        {
            text.Inlines.Add(new Run { Text = "  " });
            text.Inlines.Add(new Run
            {
                Text = $"({row.TypeModelText})",
                Foreground = new SolidColorBrush(ProviderPalette.Color(row.Provider)),
            });
        }
        return text;
    }
}
