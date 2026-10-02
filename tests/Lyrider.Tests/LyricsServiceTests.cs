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
        Assert.IsTrue(result.IsTimeSynced);
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
        Assert.IsTrue(result.IsTimeSynced);
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
    public async Task ResolveAsync_WhenCiderLyricsExist_AutomaticPrefersNetease()
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

        Assert.AreEqual("Remote", result.Lines.Single().Text);
        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual(1, remote.RequestCount);
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
    public async Task ResolveAsync_TranslationRequestedButNoProviderHasTranslation_ReturnsPreferredOriginal()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot([new(1, null, "Remote original")], true, LyricsSource.Netease));
        using var service = new LyricsService([netease]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [new(1, null, "Cider original")],
            new LyricsResolveOptions(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual("Remote original", result.Lines.Single().Text);
        Assert.AreEqual(1, netease.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_TranslationRequestedForChineseLyrics_UsesPreferredChineseOriginal()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot(
                [new(1, null, "远程中文原文", "远程中文字段")],
                true,
                LyricsSource.Netease));
        using var service = new LyricsService([netease]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [new(1, null, "Cider 中文原文"), new(3, null, "Cider 下一句")],
            new LyricsResolveOptions(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual("远程中文原文", result.Lines[0].Text);
        Assert.IsNull(result.Lines[0].Translation);
        Assert.AreEqual(1, netease.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_ChineseOriginal_RemovesRedundantChineseTranslation()
    {
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot(
                [new(1, null, "中文原文", "中文翻译字段")],
                true,
                LyricsSource.Netease));
        using var service = new LyricsService([netease]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            [],
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.IsNull(result.Lines.Single().Translation);
    }

    [TestMethod]
    public async Task ResolveAsync_ExternalSyncedLyrics_AlignsTimestampsToCiderLyrics()
    {
        IReadOnlyList<LyricLineInfo> ciderLyrics =
        [
            new(10, 15, "First line"),
            new(20, 25, "Second line"),
            new(30, 35, "Third line")
        ];
        var netease = new FakeLyricsProvider(
            LyricsSource.Netease,
            new LyricsSnapshot(
                [
                    new(10.7, 15.7, "First line", "第一句"),
                    new(20.7, 25.7, "Second line", "第二句"),
                    new(30.7, 35.7, "Third line", "第三句")
                ],
                true,
                LyricsSource.Netease));
        using var service = new LyricsService([netease]);

        var result = await service.ResolveAsync(
            CreateTrack(),
            ciderLyrics,
            new LyricsResolveOptions(LyricsSource.Netease, true));

        Assert.AreEqual(10, result.Lines[0].StartTime, 0.001);
        Assert.AreEqual(15, result.Lines[0].EndTime!.Value, 0.001);
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
        Assert.AreEqual(3, handler.Requests.Count);
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
    public async Task ResolveAsync_PreferredSourceEmpty_FallsBack()
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

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual(1, netease.RequestCount);
        Assert.AreEqual(1, qq.RequestCount);
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

        Assert.AreEqual(1, handler.Requests.Count(request => request.Uri.Host == "lrclib.net"));
        Assert.AreEqual(3, handler.Requests.Count);
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

    [TestMethod]
    public async Task ResolveAsync_PreferredQqMusic_MovesItAheadOfNetease()
    {
        var netease = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, 5));
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 4));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.QqMusic, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual(0, netease.RequestCount);
        Assert.AreEqual(1, qq.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_PreferredCiderWithoutTranslation_FindsTranslatedFallback()
    {
        var remote = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, 4));
        using var service = new LyricsService([remote]);

        var result = await service.ResolveAsync(
            CreateTrack(), [new(1, null, "Cider original")], new(LyricsSource.Cider, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual(1, remote.RequestCount);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task ResolveAsync_TranslationCoverageBelowThreshold_ContinuesToNextSource(int translatedLines)
    {
        var netease = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, translatedLines));
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 4));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual(1, qq.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_TranslationCoverageAtThreshold_IgnoresBlankLinesAndStops()
    {
        var snapshot = TranslatedSnapshot(LyricsSource.Netease, 4);
        snapshot = snapshot with { Lines = snapshot.Lines.Append(new LyricLineInfo(9, null, " ")).ToArray() };
        var netease = new FakeLyricsProvider(LyricsSource.Netease, snapshot);
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 5));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual(0, qq.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_NoSourceReachesThreshold_ChoosesHighestCoverage()
    {
        var netease = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, 1));
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 3));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
    }

    [TestMethod]
    public async Task ResolveAsync_EqualCoverage_ChoosesSyncedThenPreferredSource()
    {
        var netease = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, 3, false));
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 3));
        var lrclib = new FakeLyricsProvider(LyricsSource.Lrclib, TranslatedSnapshot(LyricsSource.Lrclib, 3));
        using var service = new LyricsService([netease, qq, lrclib]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.AreEqual(1, lrclib.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_FullyTranslatedPlainLyrics_ContinuesToSyncedTranslatedLyrics()
    {
        var netease = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, 5, false));
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 4));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreEqual(LyricsSource.QqMusic, result.Source);
        Assert.IsTrue(result.IsTimeSynced);
    }

    [TestMethod]
    public async Task ResolveAsync_TranslationDisabled_StopsAtFirstSyncedOriginal()
    {
        var netease = new FakeLyricsProvider(LyricsSource.Netease, TranslatedSnapshot(LyricsSource.Netease, 0));
        var qq = new FakeLyricsProvider(LyricsSource.QqMusic, TranslatedSnapshot(LyricsSource.QqMusic, 5));
        using var service = new LyricsService([netease, qq]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, false));

        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual(0, qq.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_OriginalAvailable_ReportsItBeforeTranslationCompletes()
    {
        var completion = new TaskCompletionSource<LyricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = TranslatedSnapshot(LyricsSource.Netease, 0);
        var provider = new ScriptedLyricsProvider(LyricsSource.Netease, (_, onCandidate) =>
        {
            onCandidate?.Invoke(original);
            return completion.Task;
        });
        using var service = new LyricsService([provider]);
        var updates = new List<LyricsSnapshot>();

        var resolving = service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true), onProgress: updates.Add);

        Assert.IsFalse(resolving.IsCompleted);
        Assert.AreEqual(1, updates.Count);
        Assert.AreEqual(0, LyricsSelection.TranslationCoverage(updates[0]));
        completion.SetResult(TranslatedSnapshot(LyricsSource.Netease, 4));
        var result = await resolving;
        Assert.AreEqual(2, updates.Count);
        Assert.AreSame(result, updates[1]);
    }

    [TestMethod]
    public async Task ResolveAsync_CiderAvailable_ReportsItBeforePreferredSourceCompletes()
    {
        var completion = new TaskCompletionSource<LyricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new LyricsService([new ScriptedLyricsProvider(LyricsSource.Netease, (_, _) => completion.Task)]);
        var updates = new List<LyricsSnapshot>();

        var resolving = service.ResolveAsync(
            CreateTrack(), [new(1, null, "Cider original")], new(LyricsSource.Auto, true), onProgress: updates.Add);

        Assert.IsFalse(resolving.IsCompleted);
        Assert.AreEqual(LyricsSource.Cider, updates.Single().Source);
        completion.SetResult(TranslatedSnapshot(LyricsSource.Netease, 4));
        Assert.AreEqual(LyricsSource.Netease, (await resolving).Source);
    }

    [TestMethod]
    public async Task ResolveAsync_SourceTimesOutAfterOriginal_PreservesReportedLyrics()
    {
        var original = TranslatedSnapshot(LyricsSource.Netease, 0);
        var provider = new ScriptedLyricsProvider(LyricsSource.Netease, async (token, onCandidate) =>
        {
            onCandidate?.Invoke(original);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LyricsSnapshot.Empty;
        });
        using var service = new LyricsService([provider]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreSame(original.Lines, result.Lines);
    }

    [TestMethod]
    public async Task ResolveAsync_ThreeSourcesTimeOut_StillTriesLastSource()
    {
        static async Task<LyricsSnapshot> Delay(CancellationToken token, Action<LyricsSnapshot>? _)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LyricsSnapshot.Empty;
        }

        var last = new FakeLyricsProvider(LyricsSource.Lrclib, TranslatedSnapshot(LyricsSource.Lrclib, 0));
        using var service = new LyricsService([
            new ScriptedLyricsProvider(LyricsSource.Netease, Delay),
            new ScriptedLyricsProvider(LyricsSource.QqMusic, Delay),
            new ScriptedLyricsProvider(LyricsSource.Musixmatch, Delay),
            last
        ]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, false, "test-key"));

        Assert.AreEqual(LyricsSource.Lrclib, result.Source);
        Assert.AreEqual(1, last.RequestCount);
    }

    [TestMethod]
    public async Task ResolveAsync_MixedJapaneseLines_DoesNotDiscardTranslationsAsChinese()
    {
        var snapshot = new LyricsSnapshot([
            new(1, null, "夢", "梦"), new(2, null, "未来", "未来"), new(3, null, "君と歩こう", "与你同行")
        ], true, LyricsSource.Netease);
        using var service = new LyricsService([new FakeLyricsProvider(LyricsSource.Netease, snapshot)]);

        var result = await service.ResolveAsync(CreateTrack(), [], new(LyricsSource.Auto, true));

        Assert.AreEqual("梦", result.Lines[0].Translation);
        Assert.AreEqual("与你同行", result.Lines[2].Translation);
    }

    [DataTestMethod]
    [DataRow(LyricsSource.Netease, "empty")]
    [DataRow(LyricsSource.Netease, "original")]
    [DataRow(LyricsSource.Netease, "partial")]
    [DataRow(LyricsSource.Netease, "malformed")]
    [DataRow(LyricsSource.QqMusic, "empty")]
    [DataRow(LyricsSource.QqMusic, "original")]
    [DataRow(LyricsSource.QqMusic, "partial")]
    [DataRow(LyricsSource.QqMusic, "malformed")]
    public async Task FetchAsync_FirstCandidateInsufficient_TriesNextCandidate(LyricsSource source, string firstResult)
    {
        var handler = new RouteHttpMessageHandler(request =>
        {
            if (request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal))
            {
                return CandidateSearch(source);
            }

            var first = request.Uri.Query.Contains("id=41", StringComparison.Ordinal) ||
                request.Uri.Query.Contains("songmid=first", StringComparison.Ordinal);
            return first && firstResult == "malformed"
                ? Json("not-json")
                : CandidateLyrics(source, first ? firstResult : "translated");
        });
        using var client = new HttpClient(handler);
        var provider = CreateProvider(source, client);
        var updates = new List<LyricsSnapshot>();

        var result = await provider.FetchAsync([LyricsSearchTrack.From(CreateTrack())], true, null, CancellationToken.None, updates.Add);

        Assert.AreEqual(0.8, LyricsSelection.TranslationCoverage(result), 0.001);
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.IsTrue(updates.Count > 0);
    }

    [DataTestMethod]
    [DataRow(LyricsSource.Netease, HttpStatusCode.Unauthorized)]
    [DataRow(LyricsSource.Netease, HttpStatusCode.Forbidden)]
    [DataRow(LyricsSource.QqMusic, HttpStatusCode.Unauthorized)]
    [DataRow(LyricsSource.QqMusic, HttpStatusCode.Forbidden)]
    public async Task FetchAsync_FirstCandidateHttpFailure_TriesNextCandidate(LyricsSource source, HttpStatusCode status)
    {
        var handler = new RouteHttpMessageHandler(request =>
        {
            if (request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal))
            {
                return CandidateSearch(source);
            }

            var first = request.Uri.Query.Contains("id=41", StringComparison.Ordinal) ||
                request.Uri.Query.Contains("songmid=first", StringComparison.Ordinal);
            return first ? new HttpResponseMessage(status) : CandidateLyrics(source, "translated");
        });
        using var client = new HttpClient(handler);

        var result = await CreateProvider(source, client).FetchAsync(
            [LyricsSearchTrack.From(CreateTrack())], true, null, CancellationToken.None);

        Assert.IsTrue(LyricsSelection.IsSatisfactory(result, true));
        Assert.AreEqual(3, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow(LyricsSource.Netease)]
    [DataRow(LyricsSource.QqMusic)]
    public async Task FetchAsync_RepeatedCandidateAcrossAliases_FetchesLyricsOnlyOnce(LyricsSource source)
    {
        var handler = new RouteHttpMessageHandler(request =>
            request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                ? CandidateSearch(source)
                : CandidateLyrics(source, "partial"));
        using var client = new HttpClient(handler);
        var track = LyricsSearchTrack.From(CreateTrack());

        var result = await CreateProvider(source, client).FetchAsync(
            [track, track], true, null, CancellationToken.None);

        Assert.AreEqual(0.2, LyricsSelection.TranslationCoverage(result), 0.001);
        Assert.AreEqual(2, handler.Requests.Count(request => !request.Uri.AbsolutePath.Contains("search", StringComparison.Ordinal)));
        Assert.AreEqual(4, handler.Requests.Count);
    }

    private static ILyricsProvider CreateProvider(LyricsSource source, HttpClient client) => source switch
    {
        LyricsSource.Netease => new NeteaseLyricsProvider(client),
        LyricsSource.QqMusic => new QqMusicLyricsProvider(client),
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static HttpResponseMessage CandidateSearch(LyricsSource source) => Json(source == LyricsSource.Netease
        ? """{"result":{"songs":[{"id":41,"name":"Test Song","duration":120000,"artists":[{"name":"Test Artist"}]},{"id":41,"name":"Test Song","duration":120000,"artists":[{"name":"Test Artist"}]},{"id":42,"name":"Test Song","duration":120000,"artists":[{"name":"Test Artist"}]},{"id":43,"name":"Test Song (Live)","duration":120000,"artists":[{"name":"Test Artist"}]}]}}"""
        : """{"data":{"song":{"list":[{"songmid":"first","songname":"Test Song","interval":120,"singer":[{"name":"Test Artist"}]},{"songmid":"first","songname":"Test Song","interval":120,"singer":[{"name":"Test Artist"}]},{"songmid":"second","songname":"Test Song","interval":120,"singer":[{"name":"Test Artist"}]},{"songmid":"live","songname":"Test Song (Live)","interval":120,"singer":[{"name":"Test Artist"}]}]}}}""");

    private static HttpResponseMessage CandidateLyrics(LyricsSource source, string kind)
    {
        var original = kind == "empty" ? "" : "[00:01.00]Line 0\n[00:02.00]Line 1\n[00:03.00]Line 2\n[00:04.00]Line 3\n[00:05.00]Line 4";
        var translation = kind switch
        {
            "partial" => "[00:01.00]第一句",
            "translated" => "[00:01.00]第一句\n[00:02.00]第二句\n[00:03.00]第三句\n[00:04.00]第四句",
            _ => ""
        };
        return Json(source == LyricsSource.Netease
            ? System.Text.Json.JsonSerializer.Serialize(new { lrc = new { lyric = original }, tlyric = new { lyric = translation } })
            : System.Text.Json.JsonSerializer.Serialize(new { lyric = original, trans = translation }));
    }

    private static LyricsSnapshot TranslatedSnapshot(LyricsSource source, int translatedLines, bool synced = true) => new(
        Enumerable.Range(0, 5).Select(index => new LyricLineInfo(
            index + 1, index + 2, $"Line {index}", index < translatedLines ? $"译文 {index}" : null)).ToArray(),
        synced,
        source);

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
            CancellationToken cancellationToken,
            Action<LyricsSnapshot>? onCandidate = null)
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
            CancellationToken cancellationToken,
            Action<LyricsSnapshot>? onCandidate = null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LyricsSnapshot.Empty;
        }
    }

    private sealed class ScriptedLyricsProvider(
        LyricsSource source,
        Func<CancellationToken, Action<LyricsSnapshot>?, Task<LyricsSnapshot>> fetch) : ILyricsProvider
    {
        public LyricsSource Source => source;

        public Task<LyricsSnapshot> FetchAsync(
            IReadOnlyList<LyricsSearchTrack> tracks,
            bool includeTranslation,
            string? apiKey,
            CancellationToken cancellationToken,
            Action<LyricsSnapshot>? onCandidate = null) => fetch(cancellationToken, onCandidate);
    }
}
