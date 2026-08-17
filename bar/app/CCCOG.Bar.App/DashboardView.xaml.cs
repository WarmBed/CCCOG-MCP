using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var row = Ui.Row(mainText, timeText);
        // Task 9 (2026-08-17, persisted-limit card trim): a persisted-limit
        // card renders with IsFailure=false (state=Fresh — this IS the
        // app's current belief, not stale fallback data), so
        // BuildProviderRows never adds its usual failing-card status line —
        // that line, and the tooltip that lived on it, is exactly what the
        // operator asked to drop from the always-visible surface. The full
        // evidence detail must still reach the hover tooltip somehow, so
        // it's attached directly to this window row instead whenever a
        // non-failing card carries one (only the persisted-limit override
        // sets DiagnosticDetail on an otherwise-Fresh card today).
        if (!card.IsFailure && !string.IsNullOrEmpty(card.DiagnosticDetail))
        {
            ToolTipService.SetToolTip(row, card.DiagnosticDetail);
        }
        root.Children.Add(row);
        root.Children.Add(Ui.GaugeBar(card.UsedPercent));
        return root;
    }

    // ── Flow section: aligned three-column table (operator direction,
    // 2026-08-16: "doc1 | 模型 | 閒置 這樣 整齊一點 不要在右邊" — a shared
    // column grid across every row, not "flush everything to the card's
    // right edge"), fed by the flattened, depth-tagged Flow tree
    // (FlowTreeRowViewModel — one controller session per top-level row, its
    // agent/session/codex/grok children indented beneath). Two-level tree,
    // rendered from a flat list rather than a real recursive tree control:
    // depth 0 is a controller/resumable row, depth 1 is always its
    // immediately-preceding depth-0 row's child (the Rust side already
    // groups children under their controller in order — see
    // cccog_bar_core::tree::build_tree). See bar/SYNC.md for the fuller
    // derivation record.

    /// <summary>Fixed widths for the model and state/elapsed columns — the
    /// same three `ColumnDefinition`s are recreated per row (WinUI has no
    /// shared-grid-across-siblings primitive short of a real `Grid` with
    /// `Grid.Row` per entry, which would give up the `StackPanel`'s
    /// variable row heights for no benefit here) but with identical widths
    /// every time, so columns still line up visually down the whole
    /// list.</summary>
    private const double FlowModelColumnWidth = 125;
    private const double FlowStateColumnWidth = 95;

    private static FrameworkElement BuildFlowContent(IReadOnlyList<FlowTreeRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return Ui.Dim("No live sessions or recent jobs");
        }

        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
        {
            panel.Children.Add(BuildFlowRow(row));
        }

        return panel;
    }

    /// <summary>One row = title | model | state·elapsed, at consistent
    /// column positions for every row regardless of depth or kind
    /// (Claude-session controller/agent/session rows and CCCG
    /// codex/grok/terminal/resumable rows all go through this same
    /// builder). Only the title column's LEADING content (dot size +
    /// indent margin) varies by <see cref="FlowTreeRowViewModel.Depth"/> —
    /// the model and state columns are separate, fixed-width `Grid`
    /// columns, so a child's indent never shifts them out of alignment
    /// with its parent's.</summary>
    private static Microsoft.UI.Xaml.Controls.Grid BuildFlowRow(FlowTreeRowViewModel row)
    {
        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FlowModelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FlowStateColumnWidth) });

        var titleCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = row.Depth > 0 ? new Thickness(18, 0, 0, 0) : new Thickness(0),
        };
        titleCell.Children.Add(new Ellipse
        {
            Width = row.Depth > 0 ? 6 : 8,
            Height = row.Depth > 0 ? 6 : 8,
            // Task 12: built here, on the UI thread, from the plain
            // `Provider` string — not carried pre-built on the row anymore
            // (that construction used to happen on the background fetch
            // thread and threw a COMException every time; see
            // FlowTreeRowViewModel.Provider's doc comment).
            Fill = new SolidColorBrush(ProviderPalette.Color(row.Provider)),
            Opacity = row.DotOpacity,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleCell.Children.Add(FlowTitleText(row));
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(titleCell, 0);
        grid.Children.Add(titleCell);

        var modelText = Ui.Text(
            string.IsNullOrEmpty(row.TypeModelText) ? "" : $"({row.TypeModelText})", 10);
        modelText.VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(row.TypeModelText))
        {
            modelText.Foreground = new SolidColorBrush(ProviderPalette.Color(row.Provider));
        }
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(modelText, 1);
        grid.Children.Add(modelText);

        var stateElapsed = string.IsNullOrEmpty(row.ElapsedText)
            ? row.StateLabel
            : $"{row.StateLabel} · {row.ElapsedText}";
        var stateText = Ui.Text(stateElapsed, 10, 0.62);
        stateText.VerticalAlignment = VerticalAlignment.Center;

        // Task 14: "ctx 36%"/"ctx 355k" as a second, equally-dim run in the
        // same state cell (a StackPanel, not a second Grid column — the
        // column count/widths this comment block above already documents
        // as "consistent... for every row" stay untouched). Only added when
        // the row actually carried usage; the tooltip lives on this run
        // alone, not the whole cell, since it's specifically what it
        // describes.
        var stateCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        stateCell.Children.Add(stateText);
        if (!string.IsNullOrEmpty(row.ContextText))
        {
            var contextText = Ui.Text(row.ContextText, 10, 0.62);
            contextText.VerticalAlignment = VerticalAlignment.Center;
            if (!string.IsNullOrEmpty(row.ContextTooltip))
            {
                ToolTipService.SetToolTip(contextText, row.ContextTooltip);
            }
            stateCell.Children.Add(contextText);
        }
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(stateCell, 2);
        grid.Children.Add(stateCell);

        return grid;
    }

    /// <summary>The title column's text only — the "(type · model)"
    /// parenthetical moved into its own column (operator's aligned-columns
    /// direction) so this is plain text again, no colored inline run.
    ///
    /// A hover tooltip carries the FULL label plus model, as a plain,
    /// unclipped string (operator follow-up, 2026-08-16: a long
    /// agent/session label ellipsized under
    /// <see cref="TextTrimming.CharacterEllipsis"/> — e.g. "Add Claude
    /// session li…" — had no way to recover the rest). Set unconditionally
    /// rather than only when the text is actually long enough to clip:
    /// WinUI has no cheap pre-layout way to know whether a given string
    /// clips at a given TextBlock's eventual rendered width, and a tooltip
    /// on already-short, unclipped text costs nothing.</summary>
    private static TextBlock FlowTitleText(FlowTreeRowViewModel row)
    {
        var text = Ui.Text(row.Label, 11);
        var fullText = string.IsNullOrEmpty(row.TypeModelText) ? row.Label : $"{row.Label} ({row.TypeModelText})";
        ToolTipService.SetToolTip(text, fullText);
        return text;
    }
}
