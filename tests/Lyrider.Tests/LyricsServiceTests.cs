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
    public async Task ResolveAsync_LrclibSource_ReturnsSyncedLyricsAndClientIdentity()
    {
        var handler = new RouteHttpMessageHandler(request => Json(
            """
            {
              "plainLyrics": "First line\nSecond line",
              "syncedLyrics": "[00:01.00]First line\n[00:03.50]Second line"
            }
            """));
        using var service = new LyricsService(handler);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Lrclib, false));

        Assert.AreEqual(LyricsSource.Lrclib, result.Source);
        Assert.IsTrue(result.IsTimeSynced);
        Assert.AreEqual(2, result.Lines.Count);
        Assert.AreEqual(3.5, result.Lines[1].StartTime);
        StringAssert.Contains(handler.Requests.Single().Uri.Query, "duration=120");
        StringAssert.StartsWith(handler.Requests.Single().UserAgent, "Lyrider/1.0");
    }

    [TestMethod]
    public async Task ResolveAsync_LrclibPlainLyrics_ReturnsUnsyncedLines()
    {
        using var service = new LyricsService(new RouteHttpMessageHandler(_ => Json(
            """{"plainLyrics":"First line\nSecond line","syncedLyrics":null}""")));

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Lrclib, false));

        Assert.IsFalse(result.IsTimeSynced);
        Assert.AreEqual(2, result.Lines.Count);
        Assert.AreEqual("Second line", result.Lines[1].Text);
    }

    [TestMethod]
    public async Task ResolveAsync_NeteaseSource_MergesTimestampTranslation()
    {
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                ? Json("""
                    {"result":{"songs":[{"id":42,"name":"Test Song","duration":120000,"artists":[{"name":"Test Artist"}]}]}}
                    """)
                : Json("""
                    {"lrc":{"lyric":"[00:01.00]Hello\n[00:03.00]World"},"tlyric":{"lyric":"[00:01.02]你好\n[00:03.00]世界"}}
                    """));
        using var service = new LyricsService(handler);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual("你好", result.Lines[0].Translation);
        Assert.AreEqual("世界", result.Lines[1].Translation);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_QqMusicSource_DecodesAndMergesTranslation()
    {
        var original = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01.00]Hello"));
        var translated = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01.00]你好"));
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("client_search", StringComparison.Ordinal)
                ? Json("""
                    {"data":{"song":{"list":[{"songmid":"mid-1","songname":"Test Song","interval":120,"singer":[{"name":"Test Artist"}]}]}}}
                    """)
                : Json($$"""{"lyric":"{{original}}","trans":"{{translated}}"}"""));
        using var service = new LyricsService(handler);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.QqMusic, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual("你好", result.Lines.Single().Translation);
    }

    [TestMethod]
    public async Task ResolveAsync_QqMusicPlainLyrics_DoesNotMistakeTextForBase64()
    {
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("client_search", StringComparison.Ordinal)
                ? Json("""
                    {"data":{"song":{"list":[{"songmid":"mid-1","songname":"Test Song","interval":120,"singer":[{"name":"Test Artist"}]}]}}}
                    """)
                : Json("""{"lyric":"Test"}"""));
        using var service = new LyricsService(handler);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.QqMusic, false));

        Assert.AreEqual("Test", result.Lines.Single().Text);
    }

    [TestMethod]
    public async Task ResolveAsync_NonChineseProviderTranslation_DoesNotDisplayTranslation()
    {
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                ? Json("""
                    {"result":{"songs":[{"id":42,"name":"Test Song","duration":120000,"artists":[{"name":"Test Artist"}]}]}}
                    """)
                : Json("""
                    {"lrc":{"lyric":"[00:01.00]Hello"},"tlyric":{"lyric":"[00:01.00]English translation"}}
                    """));
        using var service = new LyricsService(handler);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.IsNull(result.Lines.Single().Translation);
    }

    [TestMethod]
    public async Task ResolveAsync_MusixmatchSource_MatchesOriginalLinesToTranslations()
    {
        var handler = new RouteHttpMessageHandler(request => request.Uri.AbsolutePath switch
        {
            var path when path.EndsWith("matcher.track.get", StringComparison.Ordinal) => Json("""
                {"message":{"body":{"track":{"track_id":7,"track_name":"Test Song","artist_name":"Test Artist","track_length":120}}}}
                """),
            var path when path.EndsWith("track.subtitle.get", StringComparison.Ordinal) => Json("""
                {"message":{"body":{"subtitle":{"subtitle_body":"[00:01.00]Hello\n[00:03.00]World"}}}}
                """),
            _ => Json("""
                {"message":{"body":{"translations_list":[
                  {"translation":{"matched_line":"Hello","description":"你好"}},
                  {"translation":{"matched_line":"World","description":"世界"}}
                ]}}}
                """)
        });
        using var service = new LyricsService(handler);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Musixmatch, true, "secret-key"));

        Assert.AreEqual(LyricsSource.Musixmatch, result.Source);
        Assert.AreEqual("你好", result.Lines[0].Translation);
        Assert.AreEqual("世界", result.Lines[1].Translation);
        Assert.AreEqual(3, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenCiderLyricsExist_AutomaticStopsBeforeRemoteProviders()
    {
        var remote = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot([new(1, null, "Remote")], true, LyricsSource.Netease));
        using var service = new LyricsService([remote]);
        LyricLineInfo[] ciderLyrics = [new(1, null, "Cider line")];

        var result = await service.ResolveAsync(
            CreateTrack(),
            ciderLyrics,
            new LyricsResolveOptions(LyricsSource.Auto, false));

        Assert.AreSame(ciderLyrics, result.Lines);
        Assert.AreEqual(LyricsSource.Cider, result.Source);
        Assert.AreEqual(0, remote.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenTranslationRequestedAndCiderHasOnlyOriginal_UsesTranslatedRemoteLyrics()
    {
        var remote = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot(
                [new(1, null, "Remote original", "远程翻译")],
                true,
                LyricsSource.Netease));
        using var service = new LyricsService([remote]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [new(1, null, "Cider original")],
            new LyricsResolveOptions(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual(1, remote.RequestCount);
        Assert.AreEqual("远程翻译", result.Lines.Single().Translation);
    }

    [TestMethod]
    public async Task ResolveAsync_NeteaseLocalizedTitle_MatchesSameArtistAndDuration()
    {
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                ? Json("""
                    {"result":{"songs":[{"id":42,"name":"嘘じゃない","duration":258633,"artists":[{"name":"ZUTOMAYO"}]}]}}
                    """)
                : Json("""
                    {"lrc":{"lyric":"[00:01.00]嘘じゃない"},"tlyric":{"lyric":"[00:01.00]不是谎言"}}
                    """));
        using var service = new LyricsService(handler);
        var track = new NowPlayingInfo
        {
            Name = "Truth In Lies",
            ArtistName = "ZUTOMAYO",
            AlbumName = "Truth In Lies - Single",
            DurationInMillis = 258_633
        };

        var result = await service.ResolveAsync(
            track,
            [],
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual("不是谎言", result.Lines.Single().Translation);
    }

    [TestMethod]
    public async Task ResolveAsync_AppleMusicId_UsesJapanStorefrontAliasForProviderSearch()
    {
        var handler = new RouteHttpMessageHandler(request =>
        {
            if (request.Uri.Host.Equals("itunes.apple.com", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""
                    {"resultCount":1,"results":[{
                      "trackId":1746027609,
                      "trackName":"嘘じゃない",
                      "artistName":"ずっと真夜中でいいのに。",
                      "collectionName":"嘘じゃない - Single",
                      "trackTimeMillis":258633
                    }]}
                    """);
            }

            if (request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal))
            {
                return request.Body?.Contains("%E5%98%98%E3%81%98%E3%82%83%E3%81%AA%E3%81%84", StringComparison.Ordinal) == true
                    ? Json("""
                        {"result":{"songs":[{"id":42,"name":"嘘じゃない","duration":258633,"artists":[{"name":"ずっと真夜中でいいのに。"}]}]}}
                        """)
                    : Json("""{"result":{"songs":[]}}""");
            }

            return Json("""
                {"lrc":{"lyric":"[00:01.00]嘘じゃない"},"tlyric":{"lyric":"[00:01.00]不是谎言"}}
                """);
        });
        using var service = new LyricsService(handler);
        var track = new NowPlayingInfo
        {
            Name = "Truth In Lies",
            ArtistName = "ZUTOMAYO",
            AlbumName = "Truth In Lies - Single",
            DurationInMillis = 258_633,
            PlayParameters = new PlayParameters { Id = "1746027609", Kind = "song" }
        };

        var result = await service.ResolveAsync(
            track,
            [],
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual("不是谎言", result.Lines.Single().Translation);
        Assert.AreEqual(2, handler.Requests.Count(request =>
            request.Uri.Host.Equals("music.163.com", StringComparison.OrdinalIgnoreCase) &&
            request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ResolveAsync_TranslationRequestedButNoProviderHasTranslation_ReturnsCiderLyrics()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot([new(1, null, "Remote original")], true, LyricsSource.Netease));
        using var service = new LyricsService([netease]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [new(1, null, "Cider original")],
            new LyricsResolveOptions(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.Cider, result.Source);
        Assert.AreEqual("Cider original", result.Lines.Single().Text);
        Assert.AreEqual(1, netease.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_TranslationRequested_SkipsOriginalOnlyRemoteForTranslatedRemote()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot([new(1, null, "NetEase original")], true, LyricsSource.Netease));
        var qq = new FakeLyricsProvider(
            LyricsSource.QqMusic,
            new LyricsSnapshot(
                [new(1, null, "QQ original", "QQ translation")],
                true,
                LyricsSource.QqMusic));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual("QQ translation", result.Lines.Single().Translation);
        Assert.AreEqual(1, netease.RequestCount);
        Assert.AreEqual(1, qq.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_LocalizedTitleWithAmbiguousSameLengthCandidates_ReturnsNoLyrics()
    {
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                ? Json("""
                    {"result":{"songs":[
                      {"id":41,"name":"嘘じゃない","duration":258633,"artists":[{"name":"ZUTOMAYO"}]},
                      {"id":42,"name":"Another Song","duration":259000,"artists":[{"name":"ZUTOMAYO"}]}
                    ]}}
                    """)
                : Json("""{"lrc":{"lyric":"[00:01.00]Wrong"}}"""));
        using var service = new LyricsService(handler);
        var track = new NowPlayingInfo
        {
            Name = "Truth In Lies",
            ArtistName = "ZUTOMAYO",
            DurationInMillis = 258_633
        };

        var result = await service.ResolveAsync(
            track,
            [],
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.AreEqual(0, result.Lines.Count);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_Automatic_TriesProvidersInConfiguredOrder()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot([], false, LyricsSource.Netease));
        var qq = new FakeLyricsProvider(
            LyricsSource.QqMusic,
            new LyricsSnapshot([new(1, null, "QQ")], true, LyricsSource.QqMusic));
        var lrclib = new FakeLyricsProvider(
            LyricsSource.Lrclib,
            new LyricsSnapshot([new(1, null, "LRCLIB")], true, LyricsSource.Lrclib));
        using var service = new LyricsService([lrclib, qq, netease]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Auto, false));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual(1, netease.RequestCount);
        Assert.AreEqual(1, qq.RequestCount);
        Assert.AreEqual(0, lrclib.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_FixedSource_DoesNotFallback()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot([], false, LyricsSource.Netease));
        var qq = new FakeLyricsProvider(
            LyricsSource.QqMusic,
            new LyricsSnapshot([new(1, null, "QQ")], true, LyricsSource.QqMusic));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Netease, false));

        Assert.AreEqual(0, result.Lines.Count);
        Assert.AreEqual(1, netease.RequestCount);
        Assert.AreEqual(0, qq.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_MusixmatchWithoutKey_SkipsProvider()
    {
        var musixmatch = new FakeLyricsProvider(
            LyricsSource.Musixmatch,
            new LyricsSnapshot([new(1, null, "MXM")], true, LyricsSource.Musixmatch));
        using var service = new LyricsService([musixmatch]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Musixmatch, true));

        Assert.AreEqual(0, result.Lines.Count);
        Assert.AreEqual(0, musixmatch.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_MalformedProviderResponse_ReturnsEmptyResult()
    {
        using var service = new LyricsService(new RouteHttpMessageHandler(_ => Json("not-json")));

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Lrclib, false));

        Assert.AreEqual(0, result.Lines.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_RateLimitedProvider_HonorsCooldownWithoutSecondRequest()
    {
        var handler = new RouteHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return response;
        });
        using var service = new LyricsService(handler);

        await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Lrclib, false));
        await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Lrclib, false));

        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ResolveAsync_CallerCancellation_Propagates()
    {
        using var service = new LyricsService([new DelayingLyricsProvider()]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        try
        {
            await service.ResolveAsync(
                CreateTrack(),
                [],
                new LyricsResolveOptions(LyricsSource.Netease, false),
                cancellation.Token);
            Assert.Fail("Expected the caller cancellation to propagate.");
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException is the normal concrete exception from Task.Delay.
        }
    }

    [TestMethod]
    public void IsConfidentMatch_WrongVersionOrDuration_ReturnsFalse()
    {
        Assert.IsFalse(LyricsMatching.IsConfidentMatch(CreateTrack(), "Test Song (Live)", "Test Artist", 120));
        Assert.IsFalse(LyricsMatching.IsConfidentMatch(CreateTrack(), "Test Song", "Test Artist", 130));
        Assert.IsTrue(LyricsMatching.IsConfidentMatch(CreateTrack(), "Test Song", "Test Artist feat. Guest", 122));
    }

    [TestMethod]
    public void ParseSource_UnknownValue_ReturnsAutomatic()
    {
        Assert.AreEqual(LyricsSource.Auto, LyricsService.ParseSource("removed-provider"));
        Assert.AreEqual(LyricsSource.QqMusic, LyricsService.ParseSource("qqmusic"));
    }

    private static NowPlayingInfo CreateTrack() => new()
    {
        Name = "Test Song",
        ArtistName = "Test Artist",
        AlbumName = "Test Album",
        DurationInMillis = 120_000,
        HasTimeSyncedLyrics = true
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RouteHttpMessageHandler(
        Func<RequestInfo, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestInfo> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var info = new RequestInfo(
                request.RequestUri!,
                request.Headers.UserAgent.ToString(),
                body);
            Requests.Add(info);
            return responseFactory(info);
        }
    }

    private sealed record RequestInfo(Uri Uri, string UserAgent, string? Body);

    private sealed class FakeLyricsProvider(
        LyricsSource source,
        LyricsSnapshot result) : ILyricsProvider
    {
        public LyricsSource Source { get; } = source;

        public int RequestCount { get; private set; }

        public Task<LyricsSnapshot> FetchAsync(
            IReadOnlyList<LyricsSearchTrack> tracks,
            bool includeTranslation,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class DelayingLyricsProvider : ILyricsProvider
    {
        public LyricsSource Source => LyricsSource.Netease;

        public async Task<LyricsSnapshot> FetchAsync(
            IReadOnlyList<LyricsSearchTrack> tracks,
            bool includeTranslation,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LyricsSnapshot.Empty;
        }
    }
}
