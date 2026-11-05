using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class PlaybackTimelineTests
{
    [TestMethod]
    public void PositionAt_WhenPlaying_AdvancesFromTheLatestServerSample()
    {
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(
            position: 12.5,
            duration: 200,
            isPlaying: true,
            observedAt: TimeSpan.FromSeconds(10));

        var position = timeline.PositionAt(TimeSpan.FromSeconds(10.2));

        Assert.AreEqual(12.7, position, 0.001);
    }

    [TestMethod]
    public void Reset_AfterPlaybackSample_ClearsTheTimeline()
    {
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(12.5, 200, true, TimeSpan.FromSeconds(10));

        timeline.Reset();

        Assert.AreEqual(0, timeline.PositionAt(TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    public void PositionAt_WhenPaused_HoldsTheServerPosition()
    {
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(12.5, 200, false, TimeSpan.FromSeconds(10));

        var position = timeline.PositionAt(TimeSpan.FromSeconds(30));

        Assert.AreEqual(12.5, position, 0.001);
    }

    [TestMethod]
    public void PositionAt_WhenPlaybackReachesTheEnd_ClampsToDuration()
    {
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(199.8, 200, true, TimeSpan.FromSeconds(10));

        var position = timeline.PositionAt(TimeSpan.FromSeconds(11));

        Assert.AreEqual(200, position, 0.001);
    }

    [TestMethod]
    public void ProjectedPosition_SchedulesTheNextLyricAtItsBoundary()
    {
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(9, 200, true, TimeSpan.FromSeconds(100));
        IReadOnlyList<LyricLineInfo> lines =
        [
            new(0, null, "First"),
            new(10, null, "Second")
        ];

        var projectedPosition = timeline.PositionAt(TimeSpan.FromSeconds(100.75));
        var delay = LyricPresentation.DelayUntilNextLine(lines, projectedPosition);

        Assert.AreEqual(TimeSpan.FromMilliseconds(250), delay);
    }
    [TestMethod]
    public void Synchronize_StaleSnapshotAfterPreciseSample_KeepsThePreciseProjection()
    {
        // Cider's REST position is a snapshot refreshed about once a second, so a poll can
        // return a value that is up to a second old. A precise socket sample must win.
        var timeline = new PlaybackTimeline();
        timeline.SynchronizePrecise(50, 200, isPlaying: true, observedAt: TimeSpan.FromSeconds(10));

        timeline.Synchronize(50, 200, true, TimeSpan.FromSeconds(10.7));

        Assert.AreEqual(50.7, timeline.PositionAt(TimeSpan.FromSeconds(10.7)), 0.001);
    }

    [TestMethod]
    public void Synchronize_SeekAfterPreciseSample_AdoptsTheServerPosition()
    {
        var timeline = new PlaybackTimeline();
        timeline.SynchronizePrecise(50, 200, isPlaying: true, observedAt: TimeSpan.FromSeconds(10));

        timeline.Synchronize(120, 200, true, TimeSpan.FromSeconds(10.5));

        Assert.AreEqual(120, timeline.PositionAt(TimeSpan.FromSeconds(10.5)), 0.001);
    }

    [TestMethod]
    public void Synchronize_PauseAfterPreciseSample_StopsTheProjection()
    {
        var timeline = new PlaybackTimeline();
        timeline.SynchronizePrecise(50, 200, isPlaying: true, observedAt: TimeSpan.FromSeconds(10));

        timeline.Synchronize(50.4, 200, false, TimeSpan.FromSeconds(10.5));

        Assert.AreEqual(50.4, timeline.PositionAt(TimeSpan.FromSeconds(20)), 0.001);
    }

    [TestMethod]
    public void Synchronize_PreciseSampleHasExpired_AdoptsTheServerPosition()
    {
        var timeline = new PlaybackTimeline();
        timeline.SynchronizePrecise(50, 200, isPlaying: true, observedAt: TimeSpan.FromSeconds(10));

        timeline.Synchronize(55.2, 200, true, TimeSpan.FromSeconds(16));

        Assert.AreEqual(55.2, timeline.PositionAt(TimeSpan.FromSeconds(16)), 0.001);
    }

    [TestMethod]
    public void SynchronizePrecise_BeforeAnyServerSample_KeepsTheProjectionBounded()
    {
        var timeline = new PlaybackTimeline();
        timeline.Synchronize(10, 200, true, TimeSpan.FromSeconds(1));
        timeline.SynchronizePrecise(10.8, 200, isPlaying: true, observedAt: TimeSpan.FromSeconds(1.1));

        Assert.AreEqual(11.8, timeline.PositionAt(TimeSpan.FromSeconds(2.1)), 0.001);
    }
}
