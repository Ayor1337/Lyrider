using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class CiderServiceTests
{
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
