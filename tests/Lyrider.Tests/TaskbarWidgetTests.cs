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
        Assert.IsTrue(settings.ShowLyricsInTaskbar);
        Assert.IsFalse(settings.MinimizeToTrayOnClose);
        Assert.IsFalse(settings.ConvertTraditionalLyricsToSimplified);
        Assert.AreEqual("Auto", settings.LyricsSource);
        Assert.IsFalse(settings.ShowLyricsTranslation);
        Assert.IsFalse(settings.HasCompletedOnboarding);
        settings.TaskbarWidgetEnabled = true;
        settings.ShowLyricsInTaskbar = false;
        settings.MinimizeToTrayOnClose = true;
        settings.ConvertTraditionalLyricsToSimplified = true;
        settings.LyricsSource = "Netease";
        settings.ShowLyricsTranslation = true;
        settings.HasCompletedOnboarding = true;

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.TaskbarWidgetEnabled);
        Assert.IsFalse(restored.ShowLyricsInTaskbar);
        Assert.IsTrue(restored.MinimizeToTrayOnClose);
        Assert.IsTrue(restored.ConvertTraditionalLyricsToSimplified);
        Assert.AreEqual("Netease", restored.LyricsSource);
        Assert.IsTrue(restored.ShowLyricsTranslation);
        Assert.IsTrue(restored.HasCompletedOnboarding);

        var upgraded = JsonSerializer.Deserialize<AppSettings>("{}");
        Assert.IsNotNull(upgraded);
        Assert.IsTrue(upgraded.ShowLyricsInTaskbar);
        Assert.AreEqual("Auto", upgraded.LyricsSource);
        Assert.IsFalse(upgraded.ShowLyricsTranslation);
        Assert.IsFalse(upgraded.HasCompletedOnboarding);
    }

    [TestMethod]
    public void Calculate_WidgetsOnLeft_PlacesPlayerAfterWidgets()
    {
        var result = TaskbarPlacement.Calculate(
            new PixelRect(0, 1040, 1920, 1080),
            new PixelRect(8, 1040, 208, 1080),
            new PixelRect(1600, 1040, 1920, 1080),
            TaskbarAlignment.Center,
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
            new PixelRect(1720, 1040, 1920, 1080),
            TaskbarAlignment.Left,
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
            null,
            TaskbarAlignment.Center,
            324,
            54,
            3,
            18);

        Assert.AreEqual(new PixelPoint(18, 1003), result);
    }

    [TestMethod]
    public void Calculate_LeftAlignedWithoutRightWidgets_PlacesPlayerBeforeSystemTray()
    {
        var result = TaskbarPlacement.Calculate(
            new PixelRect(0, 1040, 1920, 1080),
            new PixelRect(8, 1040, 208, 1080),
            new PixelRect(1600, 1040, 1920, 1080),
            TaskbarAlignment.Left,
            216,
            40,
            2,
            12);

        Assert.AreEqual(new PixelPoint(1382, 1040), result);
    }

    [TestMethod]
    public void Calculate_LeftAlignedWithoutRightAnchor_DoesNotUseUnsafeTaskbarEdge()
    {
        var result = TaskbarPlacement.Calculate(
            new PixelRect(0, 1040, 1920, 1080),
            new PixelRect(8, 1040, 208, 1080),
            null,
            TaskbarAlignment.Left,
            216,
            40,
            2,
            12);

        Assert.IsNull(result);
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
    public void GetDisplayText_WithTranslation_ShowsOriginalAndTranslation()
    {
        var state = new TaskbarPlaybackState(
            "Song",
            "Artist",
            null,
            true,
            true,
            "Hello",
            "你好");

        var result = TaskbarPresentation.GetDisplayText(state);

        Assert.AreEqual("Hello", result.Primary);
        Assert.AreEqual("你好", result.Secondary);
    }

    [TestMethod]
    public void SelectSecondaryLyric_TranslationEnabledWithoutTranslation_ShowsNextLyric()
    {
        var result = TaskbarPresentation.SelectSecondaryLyric(
            showTranslation: true,
            translation: null,
            nextLyric: "Next line");

        Assert.AreEqual("Next line", result);
    }

    [TestMethod]
    public void SelectSecondaryLyric_TranslationEnabledWithTranslation_ShowsTranslation()
    {
        var result = TaskbarPresentation.SelectSecondaryLyric(
            showTranslation: true,
            translation: "你好",
            nextLyric: "Next line");

        Assert.AreEqual("你好", result);
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

    [TestMethod]
    public void CalculateMarqueeCycleDistance_IncludesGapAndClampsInvalidValues()
    {
        Assert.AreEqual(224, TaskbarPresentation.CalculateMarqueeCycleDistance(200, 24));
        Assert.AreEqual(200, TaskbarPresentation.CalculateMarqueeCycleDistance(200, -1));
        Assert.AreEqual(24, TaskbarPresentation.CalculateMarqueeCycleDistance(-1, 24));
    }

    [TestMethod]
    public void GetLyricTransitionDirection_AdvancingLyrics_MovesUp()
    {
        var previous = CreateLyricState("Current", "Next", 4);
        var current = CreateLyricState("Next", "Later", 5);

        Assert.AreEqual(1, TaskbarPresentation.GetLyricTransitionDirection(previous, current));
    }

    [TestMethod]
    public void GetLyricTransitionDirection_SeekingBackward_MovesDown()
    {
        var previous = CreateLyricState("Later", "Latest", 8);
        var current = CreateLyricState("Earlier", "Current", 3);

        Assert.AreEqual(-1, TaskbarPresentation.GetLyricTransitionDirection(previous, current));
    }

    [TestMethod]
    public void GetLyricTransitionDirection_TrackChanged_DoesNotAnimate()
    {
        var previous = CreateLyricState("Current", "Next", 4);
        var current = CreateLyricState("First", "Second", 0) with { Title = "Another song" };

        Assert.AreEqual(0, TaskbarPresentation.GetLyricTransitionDirection(previous, current));
    }

    private static TaskbarPlaybackState CreateLyricState(
        string currentLyric,
        string nextLyric,
        int currentLyricIndex) =>
        new(
            "Song",
            "Artist",
            null,
            true,
            true,
            currentLyric,
            nextLyric,
            currentLyricIndex);
}
