using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class TrayMenuPlacementTests
{
    [TestMethod]
    public void Calculate_AtLogicalDpi_AlignsTopLeftWithCursor()
    {
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(500, 300),
            new PixelRect(0, 0, 1920, 1080),
            168,
            76);

        Assert.AreEqual(new PixelPoint(500, 300), result);
    }

    [TestMethod]
    public void Calculate_SecondaryMonitorAt150PercentDpi_ConvertsCursorToDips()
    {
        // 光标在 150% 缩放的副屏上（DPI 144 = 96 × 1.5），物理坐标要折算成 DIP。
        var result = TrayMenuPlacement.Calculate(
            144,
            new PixelPoint(2000, 100),
            new PixelRect(1920, 0, 3840, 1440),
            160,
            76);

        Assert.AreEqual(new PixelPoint(1333, 67), result);
    }

    [TestMethod]
    public void Calculate_DpiQueryFailed_FallsBackToReferenceDpi()
    {
        // 查询失败时 DPI 为 0。若守卫写成下限 1 而非 96，坐标会被放大约 96 倍。
        var result = TrayMenuPlacement.Calculate(
            0,
            new PixelPoint(500, 300),
            new PixelRect(0, 0, 1920, 1080),
            168,
            76);

        Assert.AreEqual(new PixelPoint(500, 300), result);
    }

    [TestMethod]
    public void Calculate_DpiBelowReference_ClampsToReferenceDpi()
    {
        var result = TrayMenuPlacement.Calculate(
            48,
            new PixelPoint(500, 300),
            new PixelRect(0, 0, 1920, 1080),
            168,
            76);

        Assert.AreEqual(new PixelPoint(500, 300), result);
    }

    [TestMethod]
    public void Calculate_CursorNearRightEdge_FlipsLeftOfCursor()
    {
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(1900, 300),
            new PixelRect(0, 0, 1920, 1080),
            168,
            76);

        Assert.AreEqual(new PixelPoint(1732, 300), result);
    }

    [TestMethod]
    public void Calculate_CursorNearBottomEdge_FlipsAboveCursor()
    {
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(500, 1070),
            new PixelRect(0, 0, 1920, 1080),
            168,
            76);

        Assert.AreEqual(new PixelPoint(500, 994), result);
    }

    [TestMethod]
    public void Calculate_WindowTallerThanMonitor_PinsToMonitorTop()
    {
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(400, 300),
            new PixelRect(0, 0, 800, 600),
            200,
            700);

        Assert.AreEqual(new PixelPoint(400, 0), result);
    }

    [TestMethod]
    public void Calculate_MonitorOnNegativeOrigin_AllowsNegativeDips()
    {
        // 副屏在主屏左侧时显示器原点为负，钳制不能把菜单拉回 0。
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(-1500, 200),
            new PixelRect(-1920, 0, 0, 1080),
            168,
            76);

        Assert.AreEqual(new PixelPoint(-1500, 200), result);
    }

    [TestMethod]
    public void Calculate_TrayIconOnBottomTaskbar_PutsBottomLeftCornerAtCursor()
    {
        // 光标在底部任务栏上（工作区外）。按工作区钳制会让菜单与光标隔出任务栏高度。
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(1700, 1060),
            new PixelRect(0, 0, 1920, 1080),
            168,
            200);

        Assert.AreEqual(new PixelPoint(1700, 860), result);
    }

    [TestMethod]
    public void Calculate_TrayIconInBottomRightCorner_PutsBottomRightCornerAtCursor()
    {
        var result = TrayMenuPlacement.Calculate(
            96,
            new PixelPoint(1900, 1060),
            new PixelRect(0, 0, 1920, 1080),
            168,
            200);

        Assert.AreEqual(new PixelPoint(1732, 860), result);
    }
}
