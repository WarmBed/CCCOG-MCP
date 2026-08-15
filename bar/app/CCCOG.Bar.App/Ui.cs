using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CCCOG.Bar.App;

/// <summary>
/// Small element factories shared by every section — ported verbatim
/// (namespace only adjusted) from TokenBar-Windows
/// <c>src/TokenBar.App/Ui.cs</c> (`Card`, `Disc`, `Text`, `Dim`, `Row`,
/// `BrushFromHex`) so the CCCOG flyout's card/row/typography primitives are
/// pixel-identical to TokenBar's own. See bar/SYNC.md for the derivation
/// record. `TokenKinds` and `ShareBar` were TokenBar's Models-lens legend and
/// proportion bar — CCCOG has no models lens, so they were not ported.
/// </summary>
public static class Ui
{
    public static Border Card(
        string title, UIElement content, string? subtitle = null, UIElement? trailing = null)
    {
        var stack = new StackPanel();
        var head = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        if (subtitle is not null || trailing is not null)
        {
            var end = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (subtitle is not null)
            {
                end.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            if (trailing is not null)
            {
                end.Children.Add(trailing);
            }

            Microsoft.UI.Xaml.Controls.Grid.SetColumn(end, 1);
            head.Children.Add(end);
        }

        stack.Children.Add(head);
        stack.Children.Add(content);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = stack,
        };
    }

    public static Ellipse Disc(string hex, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = BrushFromHex(hex),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Text(string text, double size = 11, double opacity = 1.0,
        bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Dim(string text, double size = 11) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Two-column row: content left, trailing text right.</summary>
    public static Microsoft.UI.Xaml.Controls.Grid Row(UIElement left, UIElement right)
    {
        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn((FrameworkElement)right, 1);
        grid.Children.Add(right);
        return grid;
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var h = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(
            255,
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h[2..4], 16),
            Convert.ToByte(h[4..6], 16)));
    }

    /// <summary>The quota bar: TokenBar's Agent-limits gauge
    /// (<c>DashboardView.xaml.cs: GaugeBar</c>), ported without the
    /// historical-pace marker/tick — CCCOG's quota DTO carries only a used
    /// percentage, not TokenBar's pace-projection data, so the marker
    /// parameters had nothing to feed them. Height, corner radius, track
    /// color, and the 10%/25%-remaining red/amber/green thresholds are
    /// unchanged from the source.</summary>
    public static FrameworkElement GaugeBar(double usedPercent)
    {
        var fill = double.IsFinite(usedPercent) ? Math.Clamp(usedPercent, 0, 100) : 0;
        var remaining = 100 - fill;
        var color = remaining <= 10 ? "#ef4444"
            : remaining <= 25 ? "#f59e0b" : "#22c55e";
        var track = new Microsoft.UI.Xaml.Controls.Grid
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
        };
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(fill, GridUnitType.Star),
        });
        track.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(100 - fill, GridUnitType.Star),
        });
        track.Children.Add(new Border
        {
            Background = BrushFromHex(color),
            CornerRadius = new CornerRadius(2.5),
        });
        return track;
    }
}
