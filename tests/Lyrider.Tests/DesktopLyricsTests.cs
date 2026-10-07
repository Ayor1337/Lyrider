using Lyrider.Models;
using Lyrider.Services;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class DesktopLyricsTests
{
    [TestMethod]
    public void Select_NextLineMode_AlternatesSlotsWithoutMovingUpcomingLyric()
    {
        LyricLineInfo[] lines = [new(0, 2, "第一句"), new(2, 4, "第二句"), new(4, 6, "第三句"), new(6, 8, "最后一句")];
        var options = new DesktopLyricsOptions(Enabled: true, TranslationEnabled: false);
        var expected = new[]
        {
            new DesktopLyricsDisplayText("第一句", "第二句", true),
            new DesktopLyricsDisplayText("第三句", "第二句", true, true),
            new DesktopLyricsDisplayText("第三句", "最后一句", true),
            new DesktopLyricsDisplayText("", "最后一句", true, true)
        };
        for (var index = 0; index < lines.Length; index++)
            Assert.AreEqual(expected[index], DesktopLyricsPresentation.Select(
                DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, index, true, true, false), options));
        Assert.AreEqual(expected[0], DesktopLyricsPresentation.Select(
            DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 0, true, false, false), options));
    }

    [TestMethod]
    public void Build_CreditsGapsAndRepeatedText_UsesActualLyricOrdinal()
    {
        LyricLineInfo[] lines = [new(0, 1, "作词：歌手"), new(1, 2, "相同歌词"), new(2, 4, "间奏"),
            new(4, 6, "相同歌词"), new(6, 8, "下一句")];
        var first = DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 1, true, true, false);
        var second = DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 3, true, true, false);
        Assert.AreEqual(0, first.LineOrdinal);
        Assert.AreEqual(1, second.LineOrdinal);
        Assert.AreEqual(second.LineOrdinal, DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 2, true, true, false).LineOrdinal);
        var display = DesktopLyricsPresentation.Select(second, new(Enabled: true));
        Assert.AreEqual("下一句", display.Primary);
        Assert.AreEqual("相同歌词", display.Secondary);
        Assert.IsTrue(display.CurrentInSecondary);
    }

    [TestMethod]
    public void Select_AlternationSingleLineTranslationAndTitle_KeepTheirOwnDisplayModes()
    {
        var state = new DesktopLyricsState("歌曲", true, true, "当前句", "译文", "下一句", LineOrdinal: 1);
        Assert.AreEqual(new DesktopLyricsDisplayText("当前句", "译文", true),
            DesktopLyricsPresentation.Select(state, new(Enabled: true)));
        Assert.AreEqual(new DesktopLyricsDisplayText("当前句", null, true),
            DesktopLyricsPresentation.Select(state, new(Enabled: true, DoubleLineEnabled: false)));
        Assert.AreEqual(new DesktopLyricsDisplayText("下一句", "当前句", true, true),
            DesktopLyricsPresentation.Select(state with { Translation = null }, new(Enabled: true)));
        Assert.AreEqual(new DesktopLyricsDisplayText("歌曲", null, true),
            DesktopLyricsPresentation.Select(state with { CurrentLyric = null }, new(Enabled: true)));
    }

    [DataTestMethod]
    [DataRow(-1.0, -1.0, 10000.0, 10000.0, 80.0, 320.0, 80.0)]
    [DataRow(99999.0, 99999.0, 10000.0, 10000.0, 80.0, 1200.0, 480.0)]
    [DataRow(99999.0, 99999.0, 1920.0, 1040.0, 120.0, 1200.0, 480.0)]
    [DataRow(0.0, 0.0, 1920.0, 1040.0, 180.0, 320.0, 180.0)]
    [DataRow(0.0, 0.0, 240.0, 60.0, 180.0, 240.0, 60.0)]
    [DataRow(double.NaN, double.PositiveInfinity, 1920.0, 1040.0, 80.0, 960.0, 80.0)]
    public void ConstrainSize_BoundsScreenAndContent_ClampsLogicalDimensions(double width, double height,
        double availableWidth, double availableHeight, double contentHeight, double expectedWidth, double expectedHeight)
    {
        Assert.AreEqual(new DesktopLyricsSize(expectedWidth, expectedHeight),
            DesktopLyricsPlacement.ConstrainSize(width, height, availableWidth, availableHeight, contentHeight));
    }

    [TestMethod]
    public void Calculate_OversizedPositionAtMixedDpi_UsesLogicalMaximumDimensions()
    {
        var rect = DesktopLyricsPlacement.Calculate(new(-10000, 0, 0, 10000), 1.5, 100,
            new("monitor", 0.5, 0.1, 50000, 30000));
        Assert.AreEqual(1800, rect.Width);
        Assert.AreEqual(720, rect.Height);
    }

    [DataTestMethod]
    [DataRow(false, false, null)]
    [DataRow(false, true, null)]
    [DataRow(true, false, "下一句")]
    [DataRow(true, true, "译文")]
    public void Select_IndependentSwitches_SelectsAtMostOneSecondary(bool doubleLine, bool translation, string? expected)
    {
        var options = new DesktopLyricsOptions(Enabled: true, DisplayMode: DesktopLyricsDisplayMode.SingleLine,
            DoubleLineEnabled: doubleLine, TranslationEnabled: translation);
        var state = new DesktopLyricsState("歌曲", true, true, "当前句", "译文", "下一句");
        Assert.AreEqual(expected, DesktopLyricsPresentation.Select(state, options).Secondary);
        Assert.AreEqual("下一句", DesktopLyricsPresentation.Select(state with { Translation = null },
            options with { DoubleLineEnabled = true, TranslationEnabled = true }).Secondary);
        Assert.AreEqual(translation, options.ShowTranslation);
    }

    [DataTestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(false, false, true, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, true, true, true)]
    [DataRow(true, false, false, true)]
    public void IncludeLyricsTranslation_NewSwitches_RequestOnlyVisibleTranslation(
        bool mainTranslation, bool doubleLine, bool translation, bool expected)
    {
        var settings = new AppSettings { ShowLyricsTranslation = mainTranslation,
            DesktopLyrics = new(Enabled: true, DoubleLineEnabled: doubleLine, TranslationEnabled: translation) };
        Assert.AreEqual(expected, settings.IncludeLyricsTranslation);
        Assert.AreEqual(mainTranslation, settings.ShowLyricsTranslation);
        settings.DesktopLyrics = settings.DesktopLyrics with { Enabled = false };
        Assert.AreEqual(mainTranslation, settings.IncludeLyricsTranslation);
    }

    [TestMethod]
    public void Build_WordTiming_HoldsDuringGapsAndFollowsUnequalDurations()
    {
        LyricLineInfo[] lines = [new(10, 18, "你好 世界", Words:
            [new(10, 11, "你"), new(12, 15, "好"), new(16, 18, "世界")])];
        var state = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 0, true, true, false, 10);
        Assert.IsNotNull(state.Timing);
        Assert.AreEqual(0.1, state.Timing.ProgressAt(0.5, true), 0.0001);
        Assert.AreEqual(0.2, state.Timing.ProgressAt(1.5, true), 0.0001);
        Assert.AreEqual(0.3, state.Timing.ProgressAt(3.5, true), 0.0001);
        Assert.AreEqual(0.4, state.Timing.ProgressAt(5.5, true), 0.0001);
        Assert.AreEqual(0.8, state.Timing.ProgressAt(7, true), 0.0001);
        Assert.AreEqual(1, state.Timing.ProgressAt(9, true));
        Assert.AreEqual(0, state.Timing.ProgressAt(9, false));
    }

    [TestMethod]
    public void Build_SeekPauseAndGap_UsesWordPositionAndPreviewStaysUnsung()
    {
        LyricLineInfo[] lines = [new(2, 4, "你好", Words: [new(2, 3, "你"), new(3, 4, "好")]),
            new(4, 8, "间奏"), new(8, 10, "再见", Words: [new(8, 9, "再"), new(9, 10, "见")])];
        var paused = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 0, true, false, false, 3.5);
        Assert.AreEqual(0.75, paused.Timing!.ProgressAt(50, paused.IsPlaying));
        var sought = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 0, true, true, false, 2.25);
        Assert.AreEqual(0.125, sought.Timing!.ProgressAt(0, true));
        var preview = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 1, true, true, false, 5);
        Assert.AreEqual("再见", preview.CurrentLyric);
        Assert.AreEqual(0, preview.Timing!.ProgressAt(0, true));
        Assert.IsNull(DesktopLyricStateBuilder.Build("New song", "Artist", [], -1, false, true, false).Timing);
    }

    [TestMethod]
    public void Build_MissingMalformedOrMismatchedWordTiming_DoesNotEstimateProgress()
    {
        LyricLineInfo[] lines = [new(0, 4, "Only line timing"),
            new(4, 8, "你好", Words: [new(4, 6, "你")]),
            new(8, 12, "你好", Words: [new(8, 10, "你"), new(9, 11, "好")]),
            new(12, 16, "你好", Words: [new(double.NaN, 14, "你好")])];
        for (var index = 0; index < lines.Length; index++)
            Assert.IsNull(DesktopLyricStateBuilder.Build("Song", "Artist", lines, index, true, true, false).Timing);
        Assert.IsNull(DesktopLyricStateBuilder.Build("Song", "Artist", lines, 0, false, true, false).Timing);
    }

    [TestMethod]
    public void Build_CombiningCharactersEmojiAndSimplifiedText_KeepsTextElementBoundaries()
    {
        LyricLineInfo[] lines = [new(0, 4, "讓👩‍🚀e\u0301", Words:
            [new(0, 1, "讓"), new(1, 3, "👩‍🚀"), new(3, 4, "e\u0301")])];
        var state = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 0, true, true, true, 2);
        Assert.AreEqual("让👩‍🚀e\u0301", state.CurrentLyric);
        Assert.AreEqual(0.5, state.Timing!.ProgressAt(0, true), 0.0001);
    }

    [DataTestMethod]
    [DataRow(DesktopLyricsDisplayMode.Translation, "译文")]
    [DataRow(DesktopLyricsDisplayMode.NextLine, "下一句")]
    [DataRow(DesktopLyricsDisplayMode.SingleLine, null)]
    public void Select_DisplayMode_SelectsExpectedSecondary(DesktopLyricsDisplayMode mode, string? expected)
    {
        var text = DesktopLyricsPresentation.Select(new("歌曲", true, true, "当前句", "译文", "下一句"),
            new(Enabled: true, DisplayMode: mode));
        Assert.IsTrue(text.IsVisible);
        Assert.AreEqual("当前句", text.Primary);
        Assert.AreEqual(expected, text.Secondary);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void Select_MissingTranslation_ShowsNextLine(string? translation)
    {
        var text = DesktopLyricsPresentation.Select(new("歌曲", true, true, "当前句", translation, "下一句"), new(Enabled: true));
        Assert.AreEqual("当前句", text.Primary);
        Assert.AreEqual("下一句", text.Secondary);
        Assert.IsNull(DesktopLyricsPresentation.Select(new("歌曲", true, true, "最后一句", translation), new(Enabled: true)).Secondary);
    }

    [TestMethod]
    public void Build_ChineseLyricsWithoutTranslation_DoubleLineShowsNextAndAdvances()
    {
        LyricLineInfo[] lines = [new(0, 2, "夜空中最亮的星"), new(2, 4, "间奏"),
            new(4, 6, "照亮我们前行的路"), new(6, 8, "再一起唱下去")];
        var options = new DesktopLyricsOptions(Enabled: true, DoubleLineEnabled: true, TranslationEnabled: true);
        var first = DesktopLyricsPresentation.Select(
            DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 0, true, true, false), options);
        Assert.AreEqual("夜空中最亮的星", first.Primary);
        Assert.AreEqual("照亮我们前行的路", first.Secondary);
        var next = DesktopLyricsPresentation.Select(
            DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 2, true, true, false), options);
        Assert.AreEqual("再一起唱下去", next.Primary);
        Assert.AreEqual("照亮我们前行的路", next.Secondary);
        Assert.IsTrue(next.CurrentInSecondary);
        Assert.IsNull(DesktopLyricsPresentation.Select(
            DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 0, true, true, false),
            options with { DoubleLineEnabled = false }).Secondary);
    }

    [TestMethod]
    public void Select_MissingCurrentLine_UsesOnlyTitle()
    {
        var text = DesktopLyricsPresentation.Select(new("歌曲", true, true), new(Enabled: true));
        Assert.AreEqual("歌曲", text.Primary);
        Assert.IsNull(text.Secondary);
        Assert.IsTrue(text.IsVisible);
    }

    [DataTestMethod]
    [DataRow(false, true, true, false, false)]
    [DataRow(true, false, true, false, false)]
    [DataRow(true, true, false, false, true)]
    [DataRow(true, true, false, true, false)]
    [DataRow(true, true, true, true, true)]
    public void Select_PlaybackAndSettings_ControlVisibility(bool enabled, bool available, bool playing, bool hide, bool visible)
    {
        Assert.AreEqual(visible, DesktopLyricsPresentation.Select(new("歌名", available, playing),
            new(Enabled: enabled, HideWhenPaused: hide)).IsVisible);
    }

    [DataTestMethod]
    [DataRow(false, false, DesktopLyricsDisplayMode.Translation, false)]
    [DataRow(false, true, DesktopLyricsDisplayMode.Translation, true)]
    [DataRow(false, true, DesktopLyricsDisplayMode.NextLine, false)]
    [DataRow(true, false, DesktopLyricsDisplayMode.SingleLine, true)]
    public void IncludeLyricsTranslation_IndependentDesktopMode_CombinesRequestDemand(
        bool mainTranslation, bool enabled, DesktopLyricsDisplayMode mode, bool expected)
    {
        var settings = new AppSettings { ShowLyricsTranslation = mainTranslation, DesktopLyrics = new(Enabled: enabled, DisplayMode: mode) };
        Assert.AreEqual(expected, settings.IncludeLyricsTranslation);
        Assert.AreEqual(mainTranslation, settings.ShowLyricsTranslation);
    }

    [TestMethod]
    public void Build_CreditsAndGap_UsesExistingLyricFiltering()
    {
        LyricLineInfo[] lines = [new(0, null, "作词：作者"), new(2, null, "First", "第一句"),
            new(4, null, "间奏"), new(6, null, "Next", "下一句")];
        var credits = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 0, true, true, false);
        Assert.IsNull(credits.CurrentLyric);
        var current = DesktopLyricStateBuilder.Build("Song", "Artist", lines, 1, true, true, false);
        Assert.AreEqual("First", current.CurrentLyric);
        Assert.AreEqual("第一句", current.Translation);
        Assert.AreEqual("Next", current.NextLyric);
        Assert.AreEqual("Next", DesktopLyricStateBuilder.Build("Song", "Artist", lines, 2, true, true, false).CurrentLyric);
    }

    [TestMethod]
    public void Build_UnsyncedOrBeforeFirstLine_FallsBackToTitle()
    {
        LyricLineInfo[] lines = [new(5, null, "歌词")];
        Assert.IsNull(DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 0, false, true, false).CurrentLyric);
        Assert.IsNull(DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, -1, true, true, false).CurrentLyric);
        Assert.AreEqual("歌曲", DesktopLyricStateBuilder.Build("歌曲", "歌手", [], 0, true, true, false).Title);
    }

    [TestMethod]
    public void Build_TraditionalText_ConvertsLyricsWhenRequestedAndAlwaysConvertsTranslation()
    {
        LyricLineInfo[] lines = [new(0, null, "讓愛繼續", "讓愛繼續"), new(3, null, "這個世界")];
        var state = DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 0, true, true, true);
        Assert.AreEqual("让爱继续", state.CurrentLyric);
        Assert.AreEqual("让爱继续", state.Translation);
        Assert.AreEqual("这个世界", state.NextLyric);
        Assert.AreEqual("讓愛繼續", DesktopLyricStateBuilder.Build("歌曲", "歌手", lines, 0, true, true, false).CurrentLyric);
    }

    [TestMethod]
    public void Build_LocalTimelineBoundaryAndSeek_SelectsTheSameLineAsMainLyrics()
    {
        LyricLineInfo[] lines = [new(0, null, "First"), new(2, null, "Second")];
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(1.8, 60, true, TimeSpan.Zero);
        var index = LyricPresentation.FindActiveLineIndex(lines, timeline.PositionAt(TimeSpan.FromMilliseconds(250)));
        Assert.AreEqual("Second", DesktopLyricStateBuilder.Build("Song", "Artist", lines, index, true, true, false).CurrentLyric);
        timeline.Synchronize(0.4, 60, false, TimeSpan.FromSeconds(1));
        index = LyricPresentation.FindActiveLineIndex(lines, timeline.PositionAt(TimeSpan.FromSeconds(5)));
        var state = DesktopLyricStateBuilder.Build("Song", "Artist", lines, index, true, false, false);
        Assert.AreEqual("First", state.CurrentLyric);
        Assert.IsFalse(state.IsPlaying);
        Assert.IsNull(DesktopLyricStateBuilder.Build("New song", "Artist", [], -1, false, true, false).CurrentLyric);
    }

    [TestMethod]
    public void Normalize_InvalidSettings_UsesValidDefaultsAndBounds()
    {
        var options = new DesktopLyricsOptions(FontSize: double.NaN, TextColor: "invalid", BackgroundOpacity: 2,
            DisplayMode: (DesktopLyricsDisplayMode)99, Position: new("monitor", double.NaN, -2, 10)).Normalize();
        Assert.AreEqual(32, options.FontSize);
        Assert.AreEqual("#FFFFFF", options.TextColor);
        Assert.AreEqual(1, options.BackgroundOpacity);
        Assert.AreEqual(DesktopLyricsDisplayMode.Translation, options.DisplayMode);
        Assert.AreEqual(0.5, options.Position!.CenterRatio);
        Assert.AreEqual(0, options.Position.BottomRatio);
        Assert.AreEqual(320, options.Position.Width);
    }

    [TestMethod]
    public void Calculate_DefaultPosition_CentersAboveWorkAreaBottom()
    {
        Assert.AreEqual(new PixelRect(480, 892, 1440, 992), DesktopLyricsPlacement.Calculate(new(0, 0, 1920, 1040), 1, 100, null));
    }

    [TestMethod]
    public void Calculate_SavedHeight_RestoresLogicalSizeAndFitsSmallerWorkArea()
    {
        var area = new PixelRect(-1920, 0, 0, 1080);
        var position = new DesktopLyricsPosition("monitor", 0.5, 0.1, 640, 300);
        var rect = DesktopLyricsPlacement.Calculate(area, 1.5, 100, position);
        Assert.AreEqual(960, rect.Width);
        Assert.AreEqual(450, rect.Height);
        Assert.AreEqual(position, DesktopLyricsPlacement.Capture("monitor", area, rect, 1.5, saveHeight: true));
        var constrained = DesktopLyricsPlacement.Calculate(new(0, 0, 800, 400), 2, 100, position);
        Assert.AreEqual(400, constrained.Height);
        Assert.AreEqual(0, constrained.Top);
        Assert.AreEqual(480, DesktopLyricsPlacement.Calculate(area, 1, 500, position).Height);
    }

    [DataTestMethod]
    [DataRow(double.NaN, null)]
    [DataRow(-1, null)]
    [DataRow(0, null)]
    [DataRow(10, 80.0)]
    [DataRow(4000, 480.0)]
    public void Normalize_InvalidHeight_UsesAutomaticHeightOrValidBounds(double height, double? expected)
    {
        Assert.AreEqual(expected, new DesktopLyricsOptions(Position: new("monitor", 0.5, 0, 960, height))
            .Normalize().Position!.Height);
    }

    [TestMethod]
    public void Calculate_MixedDpiAndNegativeMonitor_KeepsLogicalWidthAndFitsWorkArea()
    {
        var area = new PixelRect(-2560, -100, 0, 1340);
        var position = new DesktopLyricsPosition("secondary", 0.5, 0.1, 960);
        var rect = DesktopLyricsPlacement.Calculate(area, 1.5, 100, position);
        Assert.AreEqual(1440, rect.Width);
        Assert.AreEqual(150, rect.Height);
        var captured = DesktopLyricsPlacement.Capture("secondary", area, rect, 1.5);
        Assert.AreEqual(position, captured);
        var small = DesktopLyricsPlacement.Calculate(new(0, 0, 640, 480), 2, 400, position);
        Assert.AreEqual(640, small.Width);
        Assert.AreEqual(480, small.Height);
    }

    [TestMethod]
    public void Calculate_EdgeAnchors_ClampsEntireWindow()
    {
        var area = new PixelRect(0, 0, 1920, 1040);
        var rect = DesktopLyricsPlacement.Calculate(area, 1, 100, new("screen", 1, 1, 960));
        Assert.AreEqual(new PixelRect(960, 0, 1920, 100), rect);
    }
}
