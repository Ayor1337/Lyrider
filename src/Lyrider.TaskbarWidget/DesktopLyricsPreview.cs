using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ColorConverter = System.Windows.Media.ColorConverter;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Lyrider.TaskbarWidget;

internal static class DesktopLyricsPreview
{
    public static byte[] Render(DesktopLyricsOptions options, DesktopLyricsState state, double progress)
    {
        var content = new DesktopLyricsContent();
        var text = content.Apply(state, options);
        var highlight = (Color)ColorConverter.ConvertFromString(options.HighlightColor);
        var wordSync = !DesktopLyricsPresentation.UsesTranslation(state, options) &&
            options.KaraokeEnabled && state.Timing is { Words.IsDefaultOrEmpty: false };
        content.PrimaryText.SetKaraokeProgress(wordSync && !text.CurrentInSecondary ? progress : 0, highlight);
        content.SecondaryText.SetKaraokeProgress(wordSync && text.CurrentInSecondary ? progress : 0, highlight);
        var width = options.Position?.Width ?? 960;
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = new GridLength(DesktopLyricsContent.WindowVerticalPadding - 8) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (options.Locked ? 0 : options.BackgroundOpacity)), 0, 0, 0))
        };
        Grid.SetRowSpan(backdrop, 2);
        Grid.SetRow(content, 1);
        layout.Children.Add(backdrop);
        layout.Children.Add(content);
        var surface = new Border
        {
            Padding = new Thickness(12, 4, 12, 4),
            Child = layout,
            Width = width
        };
        surface.Measure(new Size(width, DesktopLyricsPlacement.MaximumHeight));
        var height = Math.Clamp(options.Position?.Height ?? surface.DesiredSize.Height,
            DesktopLyricsPlacement.MinimumHeight, DesktopLyricsPlacement.MaximumHeight);
        if (options.Position?.Height is not null && text.Secondary is not null)
        {
            content.SetExtraLineGap(Math.Max(0, height - Math.Max(DesktopLyricsPlacement.MinimumHeight, surface.DesiredSize.Height)) * 0.2);
            surface.Measure(new Size(width, height));
        }
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
