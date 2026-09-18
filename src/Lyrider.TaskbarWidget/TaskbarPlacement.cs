namespace Lyrider.TaskbarWidget;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public int CenterX => Left + Width / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class TaskbarPlacement
{
    public static PixelPoint Calculate(
        PixelRect taskbarFrame,
        PixelRect? widgetsButton,
        int widgetWidth,
        int widgetHeight,
        int gap,
        int fallbackInset)
    {
        var y = taskbarFrame.Top + Math.Max(0, (taskbarFrame.Height - widgetHeight) / 2);
        if (widgetsButton is not { IsEmpty: false } button)
        {
            return new PixelPoint(taskbarFrame.Left + fallbackInset, y);
        }

        var x = button.CenterX < taskbarFrame.CenterX
            ? button.Right + gap
            : button.Left - widgetWidth - gap;
        return new PixelPoint(x, y);
    }

    public static PixelRect CalculateHitRegion(
        PixelRect taskbarFrame,
        PixelRect? widgetsButton,
        PixelPoint placement,
        int widgetWidth,
        int widgetHeight,
        int horizontalPadding,
        int verticalPadding)
    {
        var left = Math.Max(taskbarFrame.Left, placement.X - horizontalPadding);
        var top = Math.Max(taskbarFrame.Top, placement.Y - verticalPadding);
        var right = Math.Min(taskbarFrame.Right, placement.X + widgetWidth + horizontalPadding);
        var bottom = Math.Min(taskbarFrame.Bottom, placement.Y + widgetHeight + verticalPadding);

        if (widgetsButton is { IsEmpty: false } button)
        {
            if (button.Right <= placement.X)
            {
                left = Math.Max(left, button.Right);
            }
            else if (button.Left >= placement.X + widgetWidth)
            {
                right = Math.Min(right, button.Left);
            }
        }

        return new PixelRect(left, top, right, bottom);
    }
}
