using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class DesktopLyricsPlacementTests
{
    private static readonly PixelRect WorkArea = new(0, 72, 2560, 1600);
    private const double Scale = 1.5;
    private static readonly PixelRect Window = new(560, 1294, 2000, 1528);

    private static PixelRect Resize(PixelRect proposed, DesktopLyricsResizeEdge edges, double minimum = 100,
        Func<double, double>? autoHeight = null) =>
        DesktopLyricsPlacement.Resize(proposed, edges, WorkArea, Scale, _ => minimum, autoHeight);

    [TestMethod]
    public void Resize_RightEdgeBelowMinimum_StopsAtMinimumWidthAndKeepsLeft()
    {
        var result = Resize(Window with { Right = 600 }, DesktopLyricsResizeEdge.Right);
        Assert.AreEqual(Window.Left, result.Left);
        Assert.AreEqual(480, result.Width);
        Assert.AreEqual(Window.Bottom, result.Bottom);
    }

    [TestMethod]
    public void Resize_LeftEdgePastWorkArea_StopsAtWorkAreaAndKeepsRight()
    {
        var result = Resize(Window with { Left = -300, Right = 1200 }, DesktopLyricsResizeEdge.Left);
        Assert.AreEqual(WorkArea.Left, result.Left);
        Assert.AreEqual(1200, result.Right);
        // 宽度先触到上限时，左边停在上限处。
        var limited = Resize(Window with { Left = -300 }, DesktopLyricsResizeEdge.Left);
        Assert.AreEqual(Window.Right - 1800, limited.Left);
        Assert.AreEqual(Window.Right, limited.Right);
    }

    [TestMethod]
    public void Resize_RightEdgeBeyondMaximum_StopsAtMaximumWidth()
    {
        var result = Resize(Window with { Right = 2500 }, DesktopLyricsResizeEdge.Right);
        Assert.AreEqual(Window.Left, result.Left);
        Assert.AreEqual(1800, result.Width);
    }

    [TestMethod]
    public void Resize_BottomEdgePastWorkArea_StopsAtWorkAreaWithoutMovingTop()
    {
        var result = Resize(Window with { Bottom = 1900 }, DesktopLyricsResizeEdge.Bottom);
        Assert.AreEqual(Window.Top, result.Top);
        Assert.AreEqual(WorkArea.Bottom, result.Bottom);
    }

    [TestMethod]
    public void Resize_TopEdge_GrowsUpwardUntilMaximumHeightAndKeepsBottom()
    {
        var grown = Resize(Window with { Top = 1100 }, DesktopLyricsResizeEdge.Top);
        Assert.AreEqual(new PixelRect(Window.Left, 1100, Window.Right, Window.Bottom), grown);
        var limited = Resize(Window with { Top = 100 }, DesktopLyricsResizeEdge.Top);
        Assert.AreEqual(Window.Bottom, limited.Bottom);
        Assert.AreEqual(720, limited.Height);
    }

    [TestMethod]
    public void Resize_TopEdgeBelowContentMinimum_StopsAtContentHeight()
    {
        var result = Resize(Window with { Top = 1500 }, DesktopLyricsResizeEdge.Top, minimum: 150);
        Assert.AreEqual(Window.Bottom, result.Bottom);
        Assert.AreEqual(225, result.Height);
    }

    [TestMethod]
    public void Resize_CornerDrag_ConstrainsBothDimensions()
    {
        var result = Resize(new(560, 0, 3000, 1528), DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Right);
        Assert.AreEqual(new PixelRect(560, 1528 - 720, 560 + 1800, 1528), result);
    }

    [TestMethod]
    public void Resize_WidthOnlyWithAutoHeight_FollowsContentAndKeepsBottom()
    {
        // 变窄后换行，内容高度随宽度变化。
        double Content(double width) => width < 700 ? 200 : 120;
        var narrow = Resize(Window with { Right = 560 + 900 }, DesktopLyricsResizeEdge.Right, minimum: 0, autoHeight: Content);
        Assert.AreEqual(Window.Bottom, narrow.Bottom);
        Assert.AreEqual(300, narrow.Height);
        var wide = Resize(Window with { Right = 560 + 1500 }, DesktopLyricsResizeEdge.Right, minimum: 0, autoHeight: Content);
        Assert.AreEqual(Window.Bottom, wide.Bottom);
        Assert.AreEqual(180, wide.Height);
    }

    [TestMethod]
    public void Resize_WidthOnlyWithManualHeight_KeepsHeightUnlessContentNeedsMore()
    {
        var kept = Resize(Window with { Right = 1700 }, DesktopLyricsResizeEdge.Right, minimum: 100);
        Assert.AreEqual(Window.Height, kept.Height);
        var grown = Resize(Window with { Right = 1700 }, DesktopLyricsResizeEdge.Right, minimum: 200);
        Assert.AreEqual(Window.Bottom, grown.Bottom);
        Assert.AreEqual(300, grown.Height);
    }

    [TestMethod]
    public void Resize_AutoHeightAboveWorkArea_ShiftsDownInsideWorkArea()
    {
        var nearTop = new PixelRect(560, 80, 2000, 200);
        var result = Resize(nearTop with { Right = 1700 }, DesktopLyricsResizeEdge.Right, minimum: 0, autoHeight: _ => 300);
        Assert.AreEqual(WorkArea.Top, result.Top);
        Assert.AreEqual(450, result.Height);
    }
}
