using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class CiderPlaybackSocketTests
{
    // Captured from Cider's Socket.IO stream.
    private const string TimeFrame =
        "42[\"API:Playback\",{\"type\":\"playbackStatus.playbackTimeDidChange\",\"data\":{\"currentPlaybackTime\":70.000998,\"currentPlaybackDuration\":391.404761,\"currentPlaybackTimeRemaining\":321.403763,\"isPlaying\":true,\"anchor\":{\"startEpochMs\":1793876994384,\"endEpochMs\":1793877385789,\"currentTime\":70.000998,\"duration\":391.404761,\"timestamp\":1793877064385,\"rate\":1}}}]";

    [TestMethod]
    public void TryParseTimeEvent_CiderTimeFrame_ReadsPositionAndSampleTime()
    {
        Assert.IsTrue(CiderPlaybackSocket.TryParseTimeEvent(TimeFrame, out var sample));

        Assert.AreEqual(new PlaybackTimeSample(70.000998, 391.404761, true, 1793877064385), sample);
    }

    [TestMethod]
    public void TryParseTimeEvent_OtherFrames_AreIgnored()
    {
        Assert.IsFalse(CiderPlaybackSocket.TryParseTimeEvent("42[\"message\",\"Hello from server\"]", out _));
        Assert.IsFalse(CiderPlaybackSocket.TryParseTimeEvent("42[\"API:Playback\",{\"type\":\"playbackStatus.nowPlayingItemDidChange\",\"data\":{}}]", out _));
        Assert.IsFalse(CiderPlaybackSocket.TryParseTimeEvent("40{\"sid\":\"x\"}", out _));
        Assert.IsFalse(CiderPlaybackSocket.TryParseTimeEvent("42[\"API:Playback\",{", out _));
    }

    [TestMethod]
    public void TryParseTimeEvent_WithoutAnchor_ReportsNoSampleTime()
    {
        const string frame = "42[\"API:Playback\",{\"type\":\"playbackStatus.playbackTimeDidChange\",\"data\":{\"currentPlaybackTime\":1.5,\"currentPlaybackDuration\":200,\"isPlaying\":false}}]";

        Assert.IsTrue(CiderPlaybackSocket.TryParseTimeEvent(frame, out var sample));

        Assert.AreEqual(new PlaybackTimeSample(1.5, 200, false, 0), sample);
    }

    [TestMethod]
    public void AgeAt_RecentSample_ReturnsElapsedTime()
    {
        var sample = new PlaybackTimeSample(70, 391, true, 1_000_000);

        Assert.AreEqual(TimeSpan.FromMilliseconds(40), sample.AgeAt(1_000_040));
    }

    [TestMethod]
    public void AgeAt_ImplausibleTimestamp_FallsBackToZero()
    {
        Assert.AreEqual(TimeSpan.Zero, new PlaybackTimeSample(70, 391, true, 0).AgeAt(1_000_000));
        Assert.AreEqual(TimeSpan.Zero, new PlaybackTimeSample(70, 391, true, 1_000_100).AgeAt(1_000_000));
        Assert.AreEqual(TimeSpan.Zero, new PlaybackTimeSample(70, 391, true, 1_000_000).AgeAt(1_005_000));
    }

    [TestMethod]
    public void SocketUri_HttpBaseAddress_UsesEngineIoWebSocketEndpoint()
    {
        Assert.AreEqual(
            new Uri("ws://localhost:10767/socket.io/?EIO=4&transport=websocket"),
            CiderPlaybackSocket.SocketUri(new Uri("http://localhost:10767/")));
    }
}
