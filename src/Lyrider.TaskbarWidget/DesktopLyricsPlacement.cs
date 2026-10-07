namespace Lyrider.TaskbarWidget;

public readonly record struct DesktopLyricsSize(double Width, double Height);

public static class DesktopLyricsPlacement
{
    public const double MinimumWidth = 320;
    public const double MaximumWidth = 1200;
    public const double MinimumHeight = 80;
    public const double MaximumHeight = 480;

    public static DesktopLyricsSize ConstrainSize(double width, double height,
        double availableWidth, double availableHeight, double contentHeight = MinimumHeight)
    {
        var maxWidth = Math.Min(MaximumWidth, double.IsFinite(availableWidth) ? Math.Max(1, availableWidth) : MaximumWidth);
        var maxHeight = Math.Min(MaximumHeight, double.IsFinite(availableHeight) ? Math.Max(1, availableHeight) : MaximumHeight);
        var minHeight = Math.Min(maxHeight, Math.Max(MinimumHeight, double.IsFinite(contentHeight) ? contentHeight : MinimumHeight));
        return new(Math.Clamp(double.IsFinite(width) ? width : 960, Math.Min(MinimumWidth, maxWidth), maxWidth),
            Math.Clamp(double.IsFinite(height) ? height : minHeight, minHeight, maxHeight));
    }

    public static PixelRect Calculate(PixelRect workArea, double scale, double height,
        DesktopLyricsPosition? position)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var size = ConstrainSize(position?.Width ?? 960, Math.Max(height, position?.Height ?? height),
            workArea.Width / scale, workArea.Height / scale, height);
        var width = Math.Min(workArea.Width, Math.Max(1, (int)Math.Round(size.Width * scale)));
        var pixelHeight = Math.Min(workArea.Height, Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        var center = workArea.Left + workArea.Width * (position?.CenterRatio ?? 0.5);
        var bottomOffset = position is null ? 48 * scale : workArea.Height * position.BottomRatio;
        var left = Math.Clamp((int)Math.Round(center - width / 2.0), workArea.Left, workArea.Right - width);
        var top = Math.Clamp((int)Math.Round(workArea.Bottom - bottomOffset - pixelHeight),
            workArea.Top, workArea.Bottom - pixelHeight);
        return new(left, top, left + width, top + pixelHeight);
    }

    public static DesktopLyricsPosition Capture(string monitor, PixelRect workArea, PixelRect window, double scale,
        bool saveHeight = false) =>
        new(monitor,
            Math.Clamp((window.Left + window.Width / 2.0 - workArea.Left) / Math.Max(1, workArea.Width), 0, 1),
            Math.Clamp((workArea.Bottom - window.Bottom) / (double)Math.Max(1, workArea.Height), 0, 1),
            window.Width / Math.Max(0.01, scale),
            saveHeight ? window.Height / Math.Max(0.01, scale) : null);
}
