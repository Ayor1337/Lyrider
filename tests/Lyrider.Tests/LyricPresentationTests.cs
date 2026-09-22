using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class LyricPresentationTests
{
    [TestMethod]
    public void FindActiveLineIndex_WhenPlaybackTimePrecedesFirstLine_ReturnsMinusOne()
    {
        var lines = CreateLines(0, 10, 20);

        Assert.AreEqual(-1, LyricPresentation.FindActiveLineIndex(lines, -1));
    }

    [TestMethod]
    public void FindActiveLineIndex_WhenPlaybackTimeEqualsALineStart_ReturnsThatLine()
    {
        var lines = CreateLines(0, 10, 20);

        Assert.AreEqual(1, LyricPresentation.FindActiveLineIndex(lines, 10));
    }

    [TestMethod]
    public void FindActiveLineIndex_WhenPlaybackTimeFallsBetweenLines_ReturnsTheEarlierLine()
    {
        var lines = CreateLines(0, 10, 20);

        Assert.AreEqual(1, LyricPresentation.FindActiveLineIndex(lines, 19.9));
    }

    [TestMethod]
    public void FindActiveLineIndex_WhenPlaybackTimeExceedsLastLine_ReturnsLastIndex()
    {
        var lines = CreateLines(0, 10, 20);

        Assert.AreEqual(2, LyricPresentation.FindActiveLineIndex(lines, 600));
    }

    [TestMethod]
    public void FindActiveLineIndex_WhenLinesAreEmpty_ReturnsMinusOne()
    {
        Assert.AreEqual(-1, LyricPresentation.FindActiveLineIndex([], 42));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithChineseCredits_SkipsLeadingMetadata()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 1, "测试歌曲"),
            new(1, 2, "测试歌手"),
            new(2, 3, "歌手：测试歌手"),
            new(3, 4, "作词：某人"),
            new(4, 5, "作曲 某人"),
            new(5, null, "第一句歌词")
        ];

        Assert.AreEqual(5, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "测试歌曲", "测试歌手"));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithEnglishCreditsAndDecoration_SkipsLeadingMetadata()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 1, "♪ ♫"),
            new(1, 2, "Title: Test Song"),
            new(2, 3, "Artist - Test Artist"),
            new(3, 4, "Producer: Someone"),
            new(4, null, "First lyric")
        ];

        Assert.AreEqual(4, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Test Song", "Test Artist"));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithInstrumentalMarker_SkipsMarker()
    {
        IReadOnlyList<LyricLineInfo> lines = [new(0, 10, "纯音乐"), new(10, null, "第一句歌词")];

        Assert.AreEqual(1, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Song", "Artist"));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithOrdinaryFirstLine_ReturnsZero()
    {
        IReadOnlyList<LyricLineInfo> lines = [new(10, null, "第一句歌词")];

        Assert.AreEqual(0, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Song", "Artist"));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithCreditWordInsideLyric_DoesNotSkipLyric()
    {
        IReadOnlyList<LyricLineInfo> lines = [new(0, null, "作曲家写下夜色")];

        Assert.AreEqual(0, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Song", "Artist"));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithCreditAfterFirstLyric_StopsScanning()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 10, "第一句歌词"),
            new(10, null, "作词：某人")
        ];

        Assert.AreEqual(0, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Song", "Artist"));
    }

    [TestMethod]
    public void FindFirstTaskbarLyricIndex_WithOnlyMetadata_ReturnsLineCount()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 1, "Instrumental"),
            new(1, null, "Composer: Someone")
        ];

        Assert.AreEqual(lines.Count, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Song", "Artist"));
    }

    [TestMethod]
    public void DelayUntilNextLine_WhenNextLineIsAhead_ReturnsExactRemainingTime()
    {
        var lines = CreateLines(0, 10, 20);

        var delay = LyricPresentation.DelayUntilNextLine(lines, playbackTime: 9.75);

        Assert.AreEqual(TimeSpan.FromMilliseconds(250), delay);
    }

    [TestMethod]
    public void DelayUntilNextLine_WhenLastLineIsActive_ReturnsNull()
    {
        var lines = CreateLines(0, 10, 20);

        var delay = LyricPresentation.DelayUntilNextLine(lines, playbackTime: 20);

        Assert.IsNull(delay);
    }

    [TestMethod]
    public void StateForDistance_ReturnsActiveStateForZero()
    {
        var state = LyricPresentation.StateForDistance(0);

        Assert.AreEqual(LyricPresentation.ActiveOpacity, state.Opacity);
        Assert.AreEqual(LyricPresentation.ActiveScale, state.Scale);
    }

    [TestMethod]
    public void StateForDistance_ReturnsAdjacentStateForOne()
    {
        var state = LyricPresentation.StateForDistance(1);

        Assert.AreEqual(LyricPresentation.AdjacentOpacity, state.Opacity);
        Assert.AreEqual(LyricPresentation.InactiveScale, state.Scale);
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(5)]
    [DataRow(100)]
    public void StateForDistance_ReturnsFarStateBeyondTheAdjacentBand(int distance)
    {
        var state = LyricPresentation.StateForDistance(distance);

        Assert.AreEqual(LyricPresentation.FarOpacity, state.Opacity);
        Assert.AreEqual(LyricPresentation.InactiveScale, state.Scale);
    }

    [TestMethod]
    public void StateForIndex_WhenLyricsAreNotTimeSynced_ReturnsUniformState()
    {
        foreach (var index in new[] { 0, 3, 40 })
        {
            var state = LyricPresentation.StateForIndex(index, 3, isTimeSynced: false);

            Assert.AreEqual(LyricPresentation.UnsyncedOpacity, state.Opacity);
            Assert.AreEqual(LyricPresentation.InactiveScale, state.Scale);
        }
    }

    [TestMethod]
    public void StateForIndex_WhenThereIsNoActiveLine_ReturnsFarStateForEveryLine()
    {
        var state = LyricPresentation.StateForIndex(0, -1, isTimeSynced: true);

        Assert.AreEqual(LyricPresentation.FarOpacity, state.Opacity);
    }

    [TestMethod]
    public void StateForIndex_MatchesTheDistanceFromTheActiveLine()
    {
        Assert.AreEqual(
            LyricPresentation.StateForDistance(0),
            LyricPresentation.StateForIndex(5, 5, isTimeSynced: true));
        Assert.AreEqual(
            LyricPresentation.StateForDistance(1),
            LyricPresentation.StateForIndex(4, 5, isTimeSynced: true));
    }

    [TestMethod]
    public void WithHover_BrightensAFarLineWithoutChangingItsScale()
    {
        var far = LyricPresentation.StateForDistance(4);

        var hovered = LyricPresentation.WithHover(far, hovered: true);

        Assert.AreEqual(LyricPresentation.HoverOpacity, hovered.Opacity);
        Assert.AreEqual(far.Scale, hovered.Scale);
    }

    [TestMethod]
    public void WithHover_LeavesTheActiveLineAtFullOpacity()
    {
        var active = LyricPresentation.StateForDistance(0);

        Assert.AreEqual(active, LyricPresentation.WithHover(active, hovered: true));
    }

    [TestMethod]
    public void WithHover_WhenNotHovered_ReturnsTheOriginalState()
    {
        var far = LyricPresentation.StateForDistance(4);

        Assert.AreEqual(far, LyricPresentation.WithHover(far, hovered: false));
    }

    [TestMethod]
    public void ComputeScrollOffset_ParksTheLineCentreAtTheAnchorRatio()
    {
        const double viewportHeight = 600;
        const double lineTopInContent = 900;
        const double lineHeight = 60;

        var offset = LyricPresentation.ComputeScrollOffset(
            lineTopInContent, lineHeight, LyricPresentation.ActiveScale,
            viewportHeight, extentHeight: 4000);
        var centreInViewport = lineTopInContent
            + (lineHeight * LyricPresentation.ActiveScale / 2)
            - offset;

        Assert.AreEqual(viewportHeight * LyricPresentation.AnchorRatio, centreInViewport, 0.001);
    }

    [TestMethod]
    public void ComputeScrollOffset_IsIndependentOfTheCurrentScrollPosition()
    {
        var first = LyricPresentation.ComputeScrollOffset(900, 60, 1.0, 600, 4000);
        var second = LyricPresentation.ComputeScrollOffset(900, 60, 1.0, 600, 4000);

        Assert.AreEqual(first, second, 0.001);
    }

    [TestMethod]
    public void ComputeScrollOffset_AccountsForTheActiveLineScale()
    {
        const double lineHeight = 60;

        var unscaled = LyricPresentation.ComputeScrollOffset(900, lineHeight, 1.0, 600, 4000);
        var scaled = LyricPresentation.ComputeScrollOffset(
            900, lineHeight, LyricPresentation.ActiveScale, 600, 4000);

        Assert.AreEqual(lineHeight * (LyricPresentation.ActiveScale - 1) / 2, scaled - unscaled, 0.001);
    }

    [TestMethod]
    public void ComputeScrollOffset_WhenTargetWouldBeNegative_ClampsToZero()
    {
        var offset = LyricPresentation.ComputeScrollOffset(10, 60, 1.0, 600, 4000);

        Assert.AreEqual(0, offset);
    }

    [TestMethod]
    public void ComputeScrollOffset_WhenTargetExceedsTheExtent_ClampsToTheLastPage()
    {
        var offset = LyricPresentation.ComputeScrollOffset(3900, 60, 1.0, 600, 4000);

        Assert.AreEqual(3400, offset, 0.001);
    }

    [TestMethod]
    public void ComputeScrollOffset_WhenContentFitsTheViewport_ReturnsZero()
    {
        Assert.AreEqual(
            0,
            LyricPresentation.ComputeScrollOffset(100, 60, 1.0, 600, extentHeight: 400));
    }

    [TestMethod]
    public void TopGutter_WhenViewportHeightIsUnknown_UsesTheDefaultFallback()
    {
        Assert.AreEqual(
            LyricPresentation.DefaultViewportHeight * LyricPresentation.AnchorRatio,
            LyricPresentation.TopGutter(0));
    }

    [TestMethod]
    public void Gutters_AllowBothTheFirstAndLastLineToReachTheAnchor()
    {
        foreach (var viewportHeight in new[] { 200, 420, 900 })
        {
            var top = LyricPresentation.TopGutter(viewportHeight);
            var bottom = LyricPresentation.BottomGutter(viewportHeight);

            Assert.IsTrue(top >= viewportHeight * LyricPresentation.AnchorRatio);
            Assert.IsTrue(bottom >= viewportHeight * (1 - LyricPresentation.AnchorRatio));
        }
    }

    [TestMethod]
    public void ComputeLyricsSignature_IsStableForIdenticalContent()
    {
        Assert.AreEqual(
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true),
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWithFontSize()
    {
        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true),
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 44, true));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWhenTheSyncedFlagChanges()
    {
        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true),
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, false));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWhenChineseConversionSettingChanges()
    {
        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true, false),
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true, true));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWhenTranslationVisibilityOrTextChanges()
    {
        IReadOnlyList<LyricLineInfo> original = [new(0, null, "Hello", "你好")];
        IReadOnlyList<LyricLineInfo> edited = [new(0, null, "Hello", "您好")];

        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(original, 42, true, false, false),
            LyricPresentation.ComputeLyricsSignature(original, 42, true, false, true));
        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(original, 42, true, false, true),
            LyricPresentation.ComputeLyricsSignature(edited, 42, true, false, true));
    }

    [TestMethod]
    public void ToSimplified_WithTraditionalChinese_ConvertsLyrics()
    {
        Assert.AreEqual("听见风里的声音", ChineseTextConverter.ToSimplified("聽見風裡的聲音"));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWhenALineTextChanges()
    {
        IReadOnlyList<LyricLineInfo> original = [new(0, null, "First"), new(10, null, "Second")];
        IReadOnlyList<LyricLineInfo> edited = [new(0, null, "First"), new(10, null, "Changed")];

        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(original, 42, true),
            LyricPresentation.ComputeLyricsSignature(edited, 42, true));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWhenALineStartTimeChanges()
    {
        IReadOnlyList<LyricLineInfo> original = [new(0, null, "First")];
        IReadOnlyList<LyricLineInfo> edited = [new(0.5, null, "First")];

        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(original, 42, true),
            LyricPresentation.ComputeLyricsSignature(edited, 42, true));
    }

    [TestMethod]
    public void ComputeLyricsSignature_ChangesWithLineCount()
    {
        Assert.AreNotEqual(
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10), 42, true),
            LyricPresentation.ComputeLyricsSignature(CreateLines(0, 10, 20), 42, true));
    }

    private static IReadOnlyList<LyricLineInfo> CreateLines(params double[] startTimes) =>
        startTimes
            .Select(startTime => new LyricLineInfo(startTime, startTime + 5, $"Line {startTime}"))
            .ToArray();
}
