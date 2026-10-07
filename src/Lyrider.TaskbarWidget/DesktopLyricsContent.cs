using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ColorConverter = System.Windows.Media.ColorConverter;
using Color = System.Windows.Media.Color;

namespace Lyrider.TaskbarWidget;

public sealed class DesktopLyricsContent : Grid
{
    internal const double WindowVerticalPadding = 52;
    public OutlinedLyricText PrimaryText { get; } = new();
    public OutlinedLyricText SecondaryText { get; } = new();
    private double _baseLineGap;

    public void SetExtraLineGap(double gap)
    {
        if (RowDefinitions.Count == 0) return;
        SecondaryText.Margin = new Thickness(4, _baseLineGap + gap, 4, 8);
    }

    public DesktopLyricsContent()
    {
        Children.Add(PrimaryText);
        Children.Add(SecondaryText);
        VerticalAlignment = VerticalAlignment.Center;
    }

    public DesktopLyricsDisplayText Apply(DesktopLyricsState state, DesktopLyricsOptions options)
    {
        var text = DesktopLyricsPresentation.Select(state, options);
        var alignment = DesktopLyricsPresentation.ResolveAlignment(state, options);
        var split = alignment == DesktopLyricsAlignment.Split;
        var vertical = options.TextDirection == DesktopLyricsTextDirection.Vertical;
        var color = (Color)ColorConverter.ConvertFromString(options.TextColor);
        var translation = DesktopLyricsPresentation.UsesTranslation(state, options);
        var wordSync = !translation && options.KaraokeEnabled && state.Timing is { Words.IsDefaultOrEmpty: false };
        var currentColor = !string.IsNullOrWhiteSpace(state.CurrentLyric) && !wordSync
            ? (Color)ColorConverter.ConvertFromString(options.HighlightColor) : color;
        PrimaryText.ApplyStyle(options);
        SecondaryText.ApplyStyle(options);
        var textAlignment = alignment switch
        {
            DesktopLyricsAlignment.Left or DesktopLyricsAlignment.Split => TextAlignment.Left,
            DesktopLyricsAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        PrimaryText.SetText(text.Primary, options.FontSize, text.CurrentInSecondary ? color : currentColor, textAlignment);
        SecondaryText.SetText(text.Secondary ?? string.Empty, options.FontSize, translation || text.CurrentInSecondary ? currentColor : color,
            split ? TextAlignment.Right : textAlignment);
        PrimaryText.MinHeight = !vertical && text.CurrentInSecondary && text.Primary.Length == 0 ? options.FontSize * 1.25 + 4 : 0;
        PrimaryText.MinWidth = vertical && text.CurrentInSecondary && text.Primary.Length == 0 ? options.FontSize * 1.25 : 0;
        SecondaryText.Visibility = text.Secondary is null ? Visibility.Collapsed : Visibility.Visible;
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        SetRow(PrimaryText, 0);
        SetColumn(PrimaryText, 0);
        SetRow(SecondaryText, vertical ? 0 : 1);
        SetColumn(SecondaryText, vertical ? 1 : 0);
        Margin = alignment switch
        {
            DesktopLyricsAlignment.Split => new Thickness(20, 12, 20, 12),
            DesktopLyricsAlignment.Left or DesktopLyricsAlignment.Right => new Thickness(20, 0, 20, 0),
            _ => new Thickness(0)
        };
        if (vertical)
        {
            ColumnDefinitions.Add(new() { Width = split ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            ColumnDefinitions.Add(new() { Width = split ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            HorizontalAlignment = alignment switch
            {
                DesktopLyricsAlignment.Left => System.Windows.HorizontalAlignment.Left,
                DesktopLyricsAlignment.Right => System.Windows.HorizontalAlignment.Right,
                DesktopLyricsAlignment.Split => System.Windows.HorizontalAlignment.Stretch,
                _ => System.Windows.HorizontalAlignment.Center
            };
            PrimaryText.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            SecondaryText.HorizontalAlignment = split ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
            PrimaryText.VerticalAlignment = SecondaryText.VerticalAlignment = VerticalAlignment.Top;
            PrimaryText.MaxHeight = SecondaryText.MaxHeight = Math.Max(1,
                (options.Position?.Height ?? DesktopLyricsPlacement.MaximumHeight) - WindowVerticalPadding - Margin.Top - Margin.Bottom);
            PrimaryText.Margin = new Thickness(4, 2, 4, 8);
            SecondaryText.Margin = new Thickness(12, 2, 4, 8);
        }
        else
        {
            RowDefinitions.Add(new() { Height = GridLength.Auto });
            RowDefinitions.Add(new() { Height = GridLength.Auto });
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            PrimaryText.HorizontalAlignment = SecondaryText.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            PrimaryText.VerticalAlignment = SecondaryText.VerticalAlignment = split ? VerticalAlignment.Top : VerticalAlignment.Center;
            PrimaryText.MaxHeight = SecondaryText.MaxHeight = double.PositiveInfinity;
            PrimaryText.Margin = new Thickness(4, 2, 4, 2);
            _baseLineGap = split ? 12 : 4;
            SetExtraLineGap(0);
        }
        return text;
    }
}
