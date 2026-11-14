namespace Lyrider.TaskbarWidget;

public readonly record struct DesktopLyricsSize(double Width, double Height);

[Flags]
public enum DesktopLyricsResizeEdge
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

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

    /// <summary>
    /// 约束系统缩放循环给出的窗口矩形（物理像素）：拖动的边不越过工作区，宽高限制在允许范围内，
    /// 只拖左右边时高度由内容决定并保持底边不动。
    /// </summary>
    /// <param name="minimumHeight">给定逻辑宽度下允许的最小逻辑高度。</param>
    /// <param name="autoHeight">自动高度时给定逻辑宽度下的内容逻辑高度；手动高度时为 null。</param>
    public static PixelRect Resize(PixelRect proposed, DesktopLyricsResizeEdge edges, PixelRect workArea, double scale,
        Func<double, double> minimumHeight, Func<double, double>? autoHeight = null)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var left = proposed.Left;
        var top = proposed.Top;
        var right = proposed.Right;
        var bottom = proposed.Bottom;
        if (edges.HasFlag(DesktopLyricsResizeEdge.Left)) left = Math.Max(left, workArea.Left);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Right)) right = Math.Min(right, workArea.Right);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Top)) top = Math.Max(top, workArea.Top);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Bottom)) bottom = Math.Min(bottom, workArea.Bottom);

        var maxWidth = Math.Max(1, Math.Min((int)Math.Round(MaximumWidth * scale), workArea.Width));
        var minWidth = Math.Min((int)Math.Round(MinimumWidth * scale), maxWidth);
        var width = Math.Clamp(right - left, minWidth, maxWidth);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Left)) left = right - width;
        else right = left + width;

        var logicalWidth = width / scale;
        var maxHeight = Math.Max(1, Math.Min((int)Math.Ceiling(MaximumHeight * scale), workArea.Height));
        int Pixels(double logical) => double.IsFinite(logical) ? (int)Math.Ceiling(Math.Max(0, logical) * scale) : 0;
        var minHeight = Math.Min(Math.Max(Pixels(MinimumHeight), Pixels(minimumHeight(logicalWidth))), maxHeight);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Top) || edges.HasFlag(DesktopLyricsResizeEdge.Bottom))
        {
            var height = Math.Clamp(bottom - top, minHeight, maxHeight);
            if (edges.HasFlag(DesktopLyricsResizeEdge.Top)) top = bottom - height;
            else bottom = top + height;
        }
        else
        {
            // 只改宽度时，自动高度跟随内容换行，手动高度至少容纳内容；两种情况都保持底边位置。
            var height = Math.Clamp(autoHeight is null ? Math.Max(bottom - top, minHeight) : Pixels(autoHeight(logicalWidth)),
                minHeight, maxHeight);
            top = bottom - height;
        }

        if (top < workArea.Top)
        {
            bottom += workArea.Top - top;
            top = workArea.Top;
        }
        return new(left, top, right, bottom);
    }

    public static DesktopLyricsPosition Capture(string monitor, PixelRect workArea, PixelRect window, double scale,
        bool saveHeight = false) =>
        new(monitor,
            Math.Clamp((window.Left + window.Width / 2.0 - workArea.Left) / Math.Max(1, workArea.Width), 0, 1),
            Math.Clamp((workArea.Bottom - window.Bottom) / (double)Math.Max(1, workArea.Height), 0, 1),
            window.Width / Math.Max(0.01, scale),
            saveHeight ? window.Height / Math.Max(0.01, scale) : null);
}
