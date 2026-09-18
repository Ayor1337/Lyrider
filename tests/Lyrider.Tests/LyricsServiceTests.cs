using System.Net;
using System.Text;
using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class LyricsServiceTests
{
    [TestMethod]
    public async Task ResolveAsync_WhenCiderLyricsAreEmpty_UsesSyncedLrclibLyrics()
    {
        using var service = new LyricsService(new StubHttpMessageHandler(
            """
            {
              "plainLyrics": "First line\nSecond line",
              "syncedLyrics": "[00:01.00]First line\n[00:03.50]Second line"
            }
            """));
        var track = new NowPlayingInfo
        {
            Name = "Test Song",
            ArtistName = "Test Artist",
            AlbumName = "Test Album",
            DurationInMillis = 120_000,
            HasTimeSyncedLyrics = true
        };

        var result = await service.ResolveAsync(track, []);

        Assert.IsTrue(result.IsTimeSynced);
        Assert.AreEqual(2, result.Lines.Count);
        Assert.AreEqual(1, result.Lines[0].StartTime);
        Assert.AreEqual("First line", result.Lines[0].Text);
        Assert.AreEqual(3.5, result.Lines[1].StartTime);
        Assert.AreEqual("Second line", result.Lines[1].Text);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenOnlyPlainLyricsExist_ReturnsUnsyncedLines()
    {
        using var service = new LyricsService(new StubHttpMessageHandler(
            """
            {
              "plainLyrics": "First line\n\nSecond line",
              "syncedLyrics": null
            }
            """));

        var result = await service.ResolveAsync(CreateTrack(), []);

        Assert.IsFalse(result.IsTimeSynced);
        Assert.AreEqual(2, result.Lines.Count);
        Assert.AreEqual("First line", result.Lines[0].Text);
        Assert.AreEqual("Second line", result.Lines[1].Text);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenCiderLyricsExist_DoesNotCallLrclib()
    {
        var handler = new StubHttpMessageHandler("{}");
        using var service = new LyricsService(handler);
        LyricLineInfo[] ciderLyrics = [new(1, null, "Cider line")];

        var result = await service.ResolveAsync(CreateTrack(), ciderLyrics);

        Assert.IsTrue(result.IsTimeSynced);
        Assert.AreSame(ciderLyrics, result.Lines);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_LrclibRequest_IncludesTrackSignatureAndClientIdentity()
    {
        var handler = new StubHttpMessageHandler("{}");
        using var service = new LyricsService(handler);

        await service.ResolveAsync(CreateTrack(), []);

        Assert.AreEqual(1, handler.RequestCount);
        Assert.IsNotNull(handler.LastRequestUri);
        StringAssert.Contains(handler.LastRequestUri.Query, "track_name=Test%20Song");
        StringAssert.Contains(handler.LastRequestUri.Query, "artist_name=Test%20Artist");
        StringAssert.Contains(handler.LastRequestUri.Query, "album_name=Test%20Album");
        StringAssert.Contains(handler.LastRequestUri.Query, "duration=120");
        StringAssert.StartsWith(handler.UserAgent, "Lyrider/1.0");
    }

    private static NowPlayingInfo CreateTrack() => new()
    {
        Name = "Test Song",
        ArtistName = "Test Artist",
        AlbumName = "Test Album",
        DurationInMillis = 120_000,
        HasTimeSyncedLyrics = true
    };

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
