using System.Text.Json;
using Lyrider.Models;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class TaskbarWidgetTests
{
    [TestMethod]
    public void AppSettings_DefaultAndRoundTrip_PreservesDesktopSettings()
    {
        var settings = new AppSettings();

        Assert.IsFalse(settings.TaskbarWidgetEnabled);
        Assert.IsFalse(settings.MinimizeToTrayOnClose);
        settings.TaskbarWidgetEnabled = true;
        settings.MinimizeToTrayOnClose = true;

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.TaskbarWidgetEnabled);
        Assert.IsTrue(restored.MinimizeToTrayOnClose);
    }

    [TestMethod]
    public void Calculate_WidgetsOnLeft_PlacesPlayerAfterWidgets()
    {
        var result = TaskbarPlacement.Calculate(
            new PixelRect(0, 1040, 1920, 1080),
            new PixelRect(8, 1040, 208, 1080),
            216,
            40,
            2,
            12);

        Assert.AreEqual(new PixelPoint(210, 1040), result);
    }

    [TestMethod]
    public void Calculate_WidgetsOnRight_PlacesPlayerBeforeWidgets()
    {
        var result = TaskbarPlacement.Calculate(
            new PixelRect(0, 1040, 1920, 1080),
            new PixelRect(1712, 1040, 1912, 1080),
            216,
            40,
            2,
            12);

        Assert.AreEqual(new PixelPoint(1494, 1040), result);
    }

    [TestMethod]
    public void Calculate_NoWidgets_UsesScaledFallbackInsetAndCentersVertically()
    {
        var result = TaskbarPlacement.Calculate(
            new PixelRect(0, 1000, 2560, 1060),
            null,
            324,
            54,
            3,
            18);

        Assert.AreEqual(new PixelPoint(18, 1003), result);
    }

    [TestMethod]
    public void CalculateHitRegion_WithoutPadding_MatchesVisibleBackgroundBounds()
    {
        var result = TaskbarPlacement.CalculateHitRegion(
            new PixelRect(0, 1000, 2560, 1072),
            new PixelRect(9, 1000, 237, 1072),
            new PixelPoint(240, 1006),
            324,
            60,
            0,
            0);

        Assert.AreEqual(new PixelRect(240, 1006, 564, 1066), result);
    }

    [TestMethod]
    public void Presentation_PlaybackState_SelectsVisibilityAndGlyph()
    {
        Assert.IsFalse(TaskbarPresentation.ShouldShow(TaskbarPlaybackState.Unavailable));
        Assert.AreEqual("\uE768", TaskbarPresentation.GetPlayPauseGlyph(false));
        Assert.AreEqual("\uE769", TaskbarPresentation.GetPlayPauseGlyph(true));
        Assert.IsTrue(TaskbarPresentation.ShouldShow(new TaskbarPlaybackState(
            "Song",
            "Artist",
            null,
            false,
            true)));
    }

    [TestMethod]
    public void GetDisplayText_WithSyncedLyrics_ShowsCurrentAndNextLines()
    {
        var state = new TaskbarPlaybackState(
            "Song",
            "Artist",
            null,
            true,
            true,
            " Current line ",
            " Next line ");

        var result = TaskbarPresentation.GetDisplayText(state);

        Assert.AreEqual("Current line", result.Primary);
        Assert.AreEqual("Next line", result.Secondary);
    }

    [TestMethod]
    public void GetDisplayText_WithoutCurrentLyric_FallsBackToTrackMetadata()
    {
        var state = new TaskbarPlaybackState(
            "Song",
            "Artist",
            null,
            true,
            true,
            "   ",
            "Next line");

        var result = TaskbarPresentation.GetDisplayText(state);

        Assert.AreEqual("Song", result.Primary);
        Assert.AreEqual("Artist", result.Secondary);
    }

    [TestMethod]
    public void GetDisplayText_LastLyricLine_LeavesSecondaryLineEmpty()
    {
        var state = new TaskbarPlaybackState(
            "Song",
            "Artist",
            null,
            true,
            true,
            "Last line");

        var result = TaskbarPresentation.GetDisplayText(state);

        Assert.AreEqual("Last line", result.Primary);
        Assert.AreEqual(string.Empty, result.Secondary);
    }

    [TestMethod]
    public void CalculateMarqueeDistance_OnlyReturnsOverflowWidth()
    {
        Assert.AreEqual(0, TaskbarPresentation.CalculateMarqueeDistance(120, 160));
        Assert.AreEqual(0, TaskbarPresentation.CalculateMarqueeDistance(160, 160));
        Assert.AreEqual(40, TaskbarPresentation.CalculateMarqueeDistance(200, 160));
    }
}
