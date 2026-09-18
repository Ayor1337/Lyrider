namespace Lyrider.TaskbarWidget;

/// <summary>
/// 托盘菜单的屏幕定位。纯计算，不依赖 WPF 与 Win32，便于测试。
/// </summary>
public static class TrayMenuPlacement
{
    /// <summary>Windows 的逻辑 DPI，同时作为 DPI 查询失败时的回退值。</summary>
    public const int ReferenceDpi = 96;

    /// <summary>
    /// 计算菜单左上角位置。返回值与窗口尺寸同为 DIP；<paramref name="cursor" /> 与
    /// <paramref name="workingArea" /> 为物理像素，需由调用方按光标所在显示器采集。
    /// </summary>
    public static PixelPoint Calculate(
        int dpi,
        PixelPoint cursor,
        PixelRect workingArea,
        double windowWidth,
        double windowHeight)
    {
        // 低于逻辑 DPI 的取值只可能来自失败的查询，钳到 96 而不是 1，
        // 否则坐标会被放大约 96 倍。
        var scale = Math.Max(ReferenceDpi, dpi) / (double)ReferenceDpi;
        var workingLeft = workingArea.Left / scale;
        var workingTop = workingArea.Top / scale;
        var workingRight = workingArea.Right / scale;
        var workingBottom = workingArea.Bottom / scale;

        return new PixelPoint(
            Round(Math.Clamp(cursor.X / scale, workingLeft, Math.Max(workingLeft, workingRight - windowWidth))),
            Round(Math.Clamp(cursor.Y / scale, workingTop, Math.Max(workingTop, workingBottom - windowHeight))));
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
