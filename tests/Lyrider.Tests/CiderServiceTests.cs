using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class CiderServiceTests
{
    [TestMethod]
    public async Task GetLyricsAsync_WordTimingRequested_OptsInAndPreservesSeconds()
    {
        string? query = null;
        using var handler = new StubHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return Task.FromResult(JsonResponse("""
                {"data":{"lines":[{"start":1,"end":4,"text":"Hello world","words":[
                {"start":1,"end":2,"text":"Hello"},{"start":3,"end":4,"text":"world"},
                {"start":4,"end":3,"text":"invalid"}]}]}}
                """));
        });
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));
        var lines = await service.GetLyricsAsync("1", null, includeWordTiming: true);
        Assert.AreEqual("?words=true", query);
        Assert.AreEqual("Hello world", lines.Single().Text);
        Assert.AreEqual(2, lines[0].Words!.Count);
        Assert.AreEqual(3, lines[0].Words![1].StartTime);
        await service.GetLyricsAsync("1", null);
        Assert.AreEqual(string.Empty, query);
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task GetLyricsAsync_AuthenticationRejected_ReturnsEmptyWordLyrics(HttpStatusCode status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, (await service.GetLyricsAsync("1", null, includeWordTiming: true)).Count);
    }

    [TestMethod]
    public async Task GetLyricsAsync_MalformedWords_KeepsLineFallback()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {"lines":[{"start":1,"end":4,"text":"Hello","words":[null,"oops",{"text":"Hello","start":2}]}]}
            """)));
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));
        var line = (await service.GetLyricsAsync("1", null, includeWordTiming: true)).Single();
        Assert.AreEqual("Hello", line.Text);
        Assert.IsNull(line.Words);
    }

    [TestMethod]
    public async Task GetQueueAsync_NestedLyricsFlags_ParsesQueueMetadata()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            [
              {
                "id": "current",
                "attributes": {
                  "name": "Current",
                  "artistName": "Artist",
                  "albumName": "Album",
                  "durationInMillis": 180000,
                  "isrc": "JPE301201741",
                  "artwork": { "url": "https://example.test/{w}x{h}.jpg" },
                  "hasLyrics": true,
                  "hasTimeSyncedLyrics": true
                }
              },
              {
                "id": "next",
                "attributes": {
                  "name": "Next",
                  "artistName": "Artist",
                  "albumName": "Album",
                  "durationInMillis": 200000,
                  "hasLyrics": false,
                  "hasTimeSyncedLyrics": false
                }
              }
            ]
            """)));
        using var service = new CiderService(
            "http://example.test/",
            handler,
            TimeSpan.FromSeconds(3));

        var queue = await service.GetQueueAsync(null, "current");

        Assert.AreEqual(0, queue.CurrentIndex);
        Assert.AreEqual(2, queue.Items.Count);
        Assert.IsTrue(queue.Items[0].HasLyrics);
        Assert.IsTrue(queue.Items[0].HasTimeSyncedLyrics);
        Assert.IsFalse(queue.Items[1].HasLyrics);
        Assert.AreEqual("JPE301201741", queue.Items[0].Isrc);
        Assert.IsNull(queue.Items[1].Isrc);
        Assert.AreEqual("https://example.test/{w}x{h}.jpg", queue.Items[0].ArtworkUrl);
        Assert.IsNull(queue.Items[1].ArtworkUrl);
    }

    [TestMethod]
    public async Task GetQueueAsync_EntryWithoutName_KeepsSourceIndexOfLaterItems()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            [
              { "id": "current", "attributes": { "name": "Current" } },
              { "id": "broken", "attributes": {} },
              null,
              { "id": "next", "attributes": { "name": "Next" } }
            ]
            """)));
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));

        var queue = await service.GetQueueAsync(null, "current");

        Assert.IsNotNull(queue);
        CollectionAssert.AreEqual(new[] { "current", "next" }, queue.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(3, queue.Items[1].Index);
        Assert.AreEqual("—", queue.Items[1].ArtistName);
    }

    [TestMethod]
    public async Task GetQueueAsync_EmptyQueue_ReturnsEmptySnapshot()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("[]")));
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));

        var queue = await service.GetQueueAsync(null, "current");

        Assert.IsNotNull(queue);
        Assert.AreEqual(0, queue.Items.Count);
        Assert.AreEqual(-1, queue.CurrentIndex);
    }

    [DataTestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "[]")]
    [DataRow(HttpStatusCode.InternalServerError, "[]")]
    [DataRow(HttpStatusCode.OK, "{\"status\":\"ok\"}")]
    [DataRow(HttpStatusCode.OK, "[{\"id\":")]
    public async Task GetQueueAsync_FailedOrMalformedResponse_ReturnsNull(HttpStatusCode status, string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));

        Assert.IsNull(await service.GetQueueAsync(null, "current"));
    }

    [TestMethod]
    public async Task GetPlaybackStatusAsync_Playing_RequestsOnlyIsPlaying()
    {
        var paths = new List<string>();
        using var handler = new StubHandler((request, _) =>
        {
            lock (paths)
            {
                paths.Add(request.RequestUri!.AbsolutePath);
            }

            return Task.FromResult(JsonResponse("""{"status":"ok","is_playing":true}"""));
        });
        using var service = new CiderService("http://example.test/", handler, TimeSpan.FromSeconds(3));

        var status = await service.GetPlaybackStatusAsync(null);

        Assert.AreEqual(new PlaybackStatus(true), status);
        CollectionAssert.AreEqual(new[] { "/api/v1/playback/is-playing" }, paths);
    }

    [TestMethod]
    public async Task GetLyricsAsync_CustomBudget_OverridesDefaultLyricsTimeout()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            return JsonResponse("""
                {
                  "data": {
                    "lines": [
                      { "start": 0, "end": 1, "text": "line" }
                    ]
                  }
                }
                """);
        });
        using var service = new CiderService(
            "http://example.test/",
            handler,
            TimeSpan.FromMilliseconds(20));

        var defaultResult = await service.GetLyricsAsync("1", null);
        var preloadedResult = await service.GetLyricsAsync(
            "1",
            null,
            requestTimeout: TimeSpan.FromMilliseconds(500));

        Assert.AreEqual(0, defaultResult.Count);
        Assert.AreEqual(1, preloadedResult.Count);
        Assert.AreEqual("line", preloadedResult[0].Text);
    }

    [TestMethod]
    public async Task GetPlaybackStatusAsync_WhenCiderDoesNotRespond_ReturnsNull()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var service = new CiderService($"http://127.0.0.1:{port}/");

        var status = await service.GetPlaybackStatusAsync(null);

        Assert.IsNull(status);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
