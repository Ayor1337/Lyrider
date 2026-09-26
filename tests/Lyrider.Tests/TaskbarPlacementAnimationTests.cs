using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class TaskbarPlacementAnimationTests
{
    [TestMethod]
    public void MoveTo_FirstPlacement_AppearsAtTargetImmediately()
    {
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(new PixelPoint(100, 10), Milliseconds(0), true);

        Assert.AreEqual(new PixelPoint(100, 10), animation.GetPosition(Milliseconds(0)));
        Assert.IsFalse(animation.IsAnimating);
    }

    [DataTestMethod]
    [DataRow(100, 180)]
    [DataRow(180, 100)]
    public void MoveTo_PositionChanges_MovesImmediatelyAndEasesOutOver250Milliseconds(int start, int target)
    {
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(new PixelPoint(start, 10), Milliseconds(0), false);
        animation.MoveTo(new PixelPoint(target, 10), Milliseconds(0), true);

        Assert.AreEqual(new PixelPoint(start, 10), animation.GetPosition(Milliseconds(0)));
        Assert.IsTrue(animation.IsAnimating);
        var early = animation.GetPosition(Milliseconds(50))!.Value.X;
        var midway = animation.GetPosition(Milliseconds(125))!.Value.X;
        Assert.IsTrue(Math.Abs(early - start) > 0, "Movement starts without a stability delay.");
        Assert.AreEqual(start + (int)((target - start) * 0.875), midway);
        Assert.IsTrue(Math.Abs(midway - start) > Math.Abs(target - midway), "The final half slows down.");
        Assert.AreEqual(new PixelPoint(target, 10), animation.GetPosition(Milliseconds(250)));
        Assert.IsFalse(animation.IsAnimating);
    }

    [TestMethod]
    public void MoveTo_TargetChangesDuringAnimation_ContinuesFromCurrentPosition()
    {
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(new PixelPoint(100, 10), Milliseconds(0), false);
        animation.MoveTo(new PixelPoint(180, 10), Milliseconds(0), true);
        var current = animation.GetPosition(Milliseconds(125));

        animation.MoveTo(new PixelPoint(80, 10), Milliseconds(125), true);

        Assert.AreEqual(current, animation.GetPosition(Milliseconds(125)));
        Assert.IsTrue(animation.GetPosition(Milliseconds(150))!.Value.X < current!.Value.X);
        Assert.AreEqual(new PixelPoint(80, 10), animation.GetPosition(Milliseconds(375)));
    }

    [TestMethod]
    public void MoveTo_SameTargetDuringAnimation_DoesNotRestartAnimation()
    {
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(new PixelPoint(100, 10), Milliseconds(0), false);
        animation.MoveTo(new PixelPoint(180, 10), Milliseconds(0), true);
        animation.MoveTo(new PixelPoint(180, 10), Milliseconds(125), true);

        Assert.AreEqual(new PixelPoint(180, 10), animation.GetPosition(Milliseconds(250)));
        Assert.IsFalse(animation.IsAnimating);
    }

    [TestMethod]
    public void MoveTo_TrayExpandsBeyondReservedGap_StartsInsideSafeBoundary()
    {
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(new PixelPoint(180, 10), Milliseconds(0), false);
        animation.MoveTo(new PixelPoint(100, 10), Milliseconds(0), true, maximumX: 124);

        Assert.AreEqual(new PixelPoint(124, 10), animation.GetPosition(Milliseconds(0)));
        for (var elapsed = 0; elapsed <= 250; elapsed += 10)
        {
            Assert.IsTrue(animation.GetPosition(Milliseconds(elapsed))!.Value.X <= 124);
        }
        Assert.AreEqual(new PixelPoint(100, 10), animation.GetPosition(Milliseconds(250)));
    }

    [TestMethod]
    public void MoveTo_AnimationDisabledOrReset_AppliesNewLayoutImmediately()
    {
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(new PixelPoint(100, 10), Milliseconds(0), false);
        animation.MoveTo(new PixelPoint(180, 10), Milliseconds(0), true);
        animation.MoveTo(new PixelPoint(300, 20), Milliseconds(100), false);
        Assert.AreEqual(new PixelPoint(300, 20), animation.GetPosition(Milliseconds(100)));
        Assert.IsFalse(animation.IsAnimating);

        animation.Reset();
        Assert.IsNull(animation.GetPosition(Milliseconds(150)));
        animation.MoveTo(new PixelPoint(400, 30), Milliseconds(150), true);
        Assert.AreEqual(new PixelPoint(400, 30), animation.GetPosition(Milliseconds(150)));
        Assert.IsFalse(animation.IsAnimating);
    }

    [DataTestMethod]
    [DataRow(1.0)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    public void Calculate_LeftAligned_Reserves24LogicalPixelsAtEachDpi(double scale)
    {
        var frame = new PixelRect(0, 0, 3840, 120);
        var tray = new PixelRect(3200, 0, 3840, 120);
        var gap = (int)Math.Round(24 * scale);
        var width = (int)Math.Round(216 * scale);
        var target = TaskbarPlacement.Calculate(frame, null, tray, TaskbarAlignment.Left,
            width, (int)Math.Round(40 * scale), gap, 12)!.Value;

        Assert.AreEqual(gap, tray.Left - target.X - width);
        var animation = new TaskbarPlacementAnimation();
        animation.MoveTo(target, Milliseconds(0), false);
        var next = target with { X = target.X + 20 };
        animation.MoveTo(next, Milliseconds(0), true);
        var current = animation.GetPosition(Milliseconds(100))!.Value;
        var region = TaskbarPlacement.CalculateHitRegion(frame, null, current, width, 40, 0, 0);
        Assert.AreEqual(current.X, region.Left);
        Assert.AreEqual(current.X + width, region.Right);
    }

    private static TimeSpan Milliseconds(double value) => TimeSpan.FromMilliseconds(value);
}
