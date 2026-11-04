namespace Lyrider.TaskbarWidget;

/// <summary>
/// 托盘菜单的屏幕定位。纯计算，不依赖 WPF 与 Win32，便于测试。
/// </summary>
public static class TrayMenuPlacement
{
    /// <summary>Windows 的逻辑 DPI，同时作为 DPI 查询失败时的回退值。</summary>
    public const int ReferenceDpi = 96;

    /// <summary>
    /// 计算菜单左上角位置，使菜单的一个角贴住光标：优先向右下展开，空间不足时
    /// 像原生弹出菜单一样向左或向上翻转。返回值与窗口尺寸同为 DIP；
    /// <paramref name="cursor" /> 与 <paramref name="monitorBounds" /> 为物理像素，
    /// 需由调用方按光标所在显示器采集。边界用整个显示器而非工作区，
    /// 因为托盘图标在任务栏上，以工作区钳制会让菜单与光标之间隔出任务栏的高度。
    /// </summary>
    public static PixelPoint Calculate(
        int dpi,
        PixelPoint cursor,
        PixelRect monitorBounds,
        double windowWidth,
        double windowHeight)
    {
        // 低于逻辑 DPI 的取值只可能来自失败的查询，钳到 96 而不是 1，
        // 否则坐标会被放大约 96 倍。
        var scale = Math.Max(ReferenceDpi, dpi) / (double)ReferenceDpi;

        return new PixelPoint(
            Round(Place(cursor.X / scale, windowWidth, monitorBounds.Left / scale, monitorBounds.Right / scale)),
            Round(Place(cursor.Y / scale, windowHeight, monitorBounds.Top / scale, monitorBounds.Bottom / scale)));
    }

    private static double Place(double cursor, double size, double min, double max)
    {
        var position = cursor + size <= max ? cursor : cursor - size;
        return Math.Clamp(position, min, Math.Max(min, max - size));
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
