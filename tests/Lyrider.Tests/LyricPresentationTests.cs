using Lyrider.Models;
using Lyrider.Services;
using Lyrider.TaskbarWidget;
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

    [DataTestMethod]
    [DataRow("纯音乐，请欣赏")]
    [DataRow("【純音樂，請您欣賞。】")]
    [DataRow("此歌曲为没有填词的纯音乐，请您欣赏")]
    [DataRow("此歌曲為沒有填詞的純音樂，請欣賞！")]
    [DataRow(" [ INSTRUMENTAL ] ")]
    public void FindTaskbarLyricIndex_WithOnlyInstrumentalAndMetadata_ReturnsMinusOne(string marker)
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 1, "Song"),
            new(1, 2, "Artist"),
            new(2, 3, "作曲：某人"),
            new(3, 10, marker),
            new(10, null, "♪ ♫")
        ];

        for (var activeIndex = 0; activeIndex < lines.Count; activeIndex++)
        {
            Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex(lines, activeIndex, "Song", "Artist"));
        }
    }

    [DataTestMethod]
    [DataRow("纯音乐，请欣赏")]
    [DataRow("[Instrumental]")]
    [DataRow("（间奏）")]
    [DataRow("[Interlude]")]
    [DataRow("")]
    [DataRow("♪ ♫")]
    public void FindTaskbarLyricIndex_DuringInterlude_ReturnsNextLyric(string marker)
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 10, "第一句歌词"),
            new(10, 20, marker),
            new(20, 30, "纯音乐"),
            new(30, 40, "第二句歌词", "Second lyric"),
            new(40, null, "第三句歌词")
        ];

        Assert.AreEqual(0, LyricPresentation.FindTaskbarLyricIndex(lines, 0, "Song", "Artist"));
        Assert.AreEqual(3, LyricPresentation.FindTaskbarLyricIndex(lines, 1, "Song", "Artist"));
        Assert.AreEqual(3, LyricPresentation.FindTaskbarLyricIndex(lines, 2, "Song", "Artist"));
        Assert.AreEqual(3, LyricPresentation.FindTaskbarLyricIndex(lines, 3, "Song", "Artist"));
        Assert.AreEqual(4, LyricPresentation.FindNextTaskbarLyricIndex(lines, 3));
    }

    [DataTestMethod]
    [DataRow("純音樂，請欣賞")]
    [DataRow("尾奏")]
    [DataRow("[Outro]")]
    public void FindTaskbarLyricIndex_DuringOutro_ReturnsLastLyric(string marker)
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 10, "第一句歌词"),
            new(10, 20, "最后一句歌词"),
            new(20, 30, marker),
            new(30, null, "♪ ♫")
        ];

        Assert.AreEqual(1, LyricPresentation.FindTaskbarLyricIndex(lines, 2, "Song", "Artist"));
        Assert.AreEqual(1, LyricPresentation.FindTaskbarLyricIndex(lines, 3, "Song", "Artist"));
        Assert.AreEqual(-1, LyricPresentation.FindNextTaskbarLyricIndex(lines, 1));
    }

    [DataTestMethod]
    [DataRow("在纯音乐中想起你")]
    [DataRow("This instrumental reminds me of you")]
    [DataRow("纯音乐，请欣赏这段人生")]
    [DataRow("Song")]
    public void FindTaskbarLyricIndex_WithMarkerWordsOrRepeatedTitle_KeepsNormalLyric(string lyric)
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 10, "第一句歌词"),
            new(10, null, lyric)
        ];

        Assert.AreEqual(1, LyricPresentation.FindTaskbarLyricIndex(lines, 1, "Song", "Artist"));
        Assert.AreEqual(1, LyricPresentation.FindNextTaskbarLyricIndex(lines, 0));
    }

    [TestMethod]
    public void FindTaskbarLyricIndex_BeforeFirstLyricOrDuringLeadingCredits_ReturnsMinusOne()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 10, "作词：某人"),
            new(10, 20, "纯音乐，请欣赏"),
            new(20, null, "第一句歌词")
        ];

        Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex(lines, -1, "Song", "Artist"));
        Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex(lines, 0, "Song", "Artist"));
        Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex(lines, 1, "Song", "Artist"));
        Assert.AreEqual(2, LyricPresentation.FindTaskbarLyricIndex(lines, 2, "Song", "Artist"));
    }

    [DataTestMethod]
    [DataRow("【制作人：某人】", 0)]
    [DataRow("【制作人：某人】", 9.999)]
    [DataRow("【制作人：某人】", 2)]
    [DataRow("混音：某人", 0)]
    [DataRow("混音：某人", 9.999)]
    [DataRow("混音：某人", 2)]
    [DataRow("词：某人", 0)]
    [DataRow("词：某人", 2)]
    [DataRow("詞：某人", 2)]
    [DataRow("曲：某人", 2)]
    [DataRow("制作：某人", 2)]
    [DataRow("製作：某人", 2)]
    [DataRow("录音：某人", 2)]
    [DataRow("錄音：某人", 2)]
    [DataRow("母带：某人", 2)]
    [DataRow("母帶：某人", 2)]
    [DataRow("[Producer: Someone]", 2)]
    [DataRow("（制作人：某人）", 2)]
    [DataRow("( 制作人：某人 )", 2)]
    public void GetTaskbarDisplayText_DuringLeadingCreditsAndGap_ShowsTrackMetadata(
        string credit, double playbackTime)
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 2, credit),
            new(2, 10, "♪ ♫"),
            new(10, null, "第一句歌词")
        ];
        var activeIndex = LyricPresentation.FindActiveLineIndex(lines, playbackTime);
        var taskbarIndex = LyricPresentation.FindTaskbarLyricIndex(lines, activeIndex, "Song", "Artist");
        var state = new TaskbarPlaybackState("Song", "Artist", null, true, true,
            taskbarIndex >= 0 ? lines[taskbarIndex].Text : null);

        var display = TaskbarPresentation.GetDisplayText(state);

        Assert.AreEqual("Song", display.Primary, $"Playback time: {playbackTime}");
        Assert.AreEqual("Artist", display.Secondary);
        Assert.AreEqual(2, LyricPresentation.FindTaskbarLyricIndex(lines,
            LyricPresentation.FindActiveLineIndex(lines, 10), "Song", "Artist"));
    }

    [DataTestMethod]
    [DataRow("制作人写下夜色")]
    [DataRow("混音里的回忆")]
    [DataRow("【作曲家写下夜色】")]
    [DataRow("词 是未说出口的心事")]
    [DataRow("曲 是记忆里的声音")]
    [DataRow("词")]
    public void FindFirstTaskbarLyricIndex_WithCreditWordsInOrdinaryLyrics_KeepsFirstLine(string lyric)
    {
        IReadOnlyList<LyricLineInfo> lines = [new(0, null, lyric)];

        Assert.AreEqual(0, LyricPresentation.FindFirstTaskbarLyricIndex(lines, "Song", "Artist"));
    }

    [TestMethod]
    public void GetTaskbarDisplayText_ReplayingLeadingCreditsAndSeeking_ShowsLyricsOnlyAtTheirTimestamp()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 1, "【制作人：某人】"),
            new(1, 2, "制作人：另一人"),
            new(2, 10, "♪ ♫"),
            new(10, null, "第一句歌词")
        ];
        double[] playbackTimes = [-1, 0, 0.5, 1, 2, 9.999, 10, 10.1, 5, 0, 10];

        foreach (var playbackTime in playbackTimes)
        {
            var activeIndex = LyricPresentation.FindActiveLineIndex(lines, playbackTime);
            var taskbarIndex = LyricPresentation.FindTaskbarLyricIndex(lines, activeIndex, "Song", "Artist");
            var state = new TaskbarPlaybackState("Song", "Artist", null, true, true,
                taskbarIndex >= 0 ? lines[taskbarIndex].Text : null);
            var display = TaskbarPresentation.GetDisplayText(state);

            Assert.AreEqual(playbackTime >= 10 ? "第一句歌词" : "Song", display.Primary,
                $"Playback time: {playbackTime}");
            Assert.AreEqual(playbackTime >= 10 ? string.Empty : "Artist", display.Secondary);
        }
    }

    [TestMethod]
    public void FindTaskbarLyricIndex_WithEmptyLyricsOrInvalidIndex_ReturnsMinusOne()
    {
        IReadOnlyList<LyricLineInfo> lines = [new(0, null, "歌词")];

        Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex([], 0, "Song", "Artist"));
        Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex(lines, -1, "Song", "Artist"));
        Assert.AreEqual(-1, LyricPresentation.FindTaskbarLyricIndex(lines, 1, "Song", "Artist"));
        Assert.AreEqual(-1, LyricPresentation.FindNextTaskbarLyricIndex(lines, -1));
        Assert.AreEqual(-1, LyricPresentation.FindNextTaskbarLyricIndex(lines, 1));
    }

    [TestMethod]
    public void FindNextTaskbarLyricIndex_WithInterlude_SkipsPlaceholderLines()
    {
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, 10, "第一句歌词"),
            new(10, 20, "纯音乐，请欣赏"),
            new(20, 30, ""),
            new(30, null, "第二句歌词")
        ];

        Assert.AreEqual(3, LyricPresentation.FindNextTaskbarLyricIndex(lines, 0));
        Assert.AreEqual(-1, LyricPresentation.FindNextTaskbarLyricIndex(lines, 3));
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
