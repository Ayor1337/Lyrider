using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class NextTrackLyricsPreloaderTests
{
    private static readonly LyricsResolveOptions DefaultOptions = new(LyricsSource.Auto, false);

    [TestMethod]
    public async Task Prepare_KaraokeOptionChanged_ReplacesCachedLineLyricsWithWordLyrics()
    {
        var requests = 0;
        using var preloader = new NextTrackLyricsPreloader((_, options, _, _, _) =>
        {
            requests++;
            return Task.FromResult(new LyricsSnapshot([new(1, 3, "Hello",
                Words: options.IncludeWordTiming ? [new(1, 3, "Hello")] : null)], true, LyricsSource.Cider));
        });
        var next = CreateItem(1, "next", "Next");
        preloader.Prepare(next, DefaultOptions, null);
        var karaoke = DefaultOptions with { IncludeWordTiming = true };
        preloader.Prepare(next, karaoke, null);
        var result = await preloader.TakeAsync(CreateNowPlaying("next", hasTimeSyncedLyrics: true), karaoke, null, CancellationToken.None);
        Assert.AreEqual(2, requests);
        Assert.AreEqual(1, result!.Lines[0].Words!.Count);
    }

    [TestMethod]
    public void SelectNext_ReliableQueue_ReturnsFirstUpcomingItem()
    {
        var current = CreateItem(2, "current", "Current");
        var next = CreateItem(3, "next", "Next");
        var later = CreateItem(4, "later", "Later");

        var selected = NextTrackLyricsPreloader.SelectNext(
            new QueueSnapshot([current, next, later], 2),
            "current");

        Assert.AreSame(next, selected);
    }

    [TestMethod]
    public void SelectNext_AmbiguousCurrentTrack_ReturnsNull()
    {
        var snapshot = new QueueSnapshot(
            [
                CreateItem(0, "same", "Song"),
                CreateItem(1, "same", "Song"),
                CreateItem(2, "next", "Next")
            ],
            1);

        var selected = NextTrackLyricsPreloader.SelectNext(snapshot, "same");

        Assert.IsNull(selected);
    }

    [TestMethod]
    public void SelectNext_MissingCurrentIndex_ReturnsNull()
    {
        var selected = NextTrackLyricsPreloader.SelectNext(
            new QueueSnapshot([CreateItem(0, "next", "Next")], -1),
            "current");

        Assert.IsNull(selected);
    }

    [TestMethod]
    public void SelectNext_CandidateWithoutId_ReturnsNull()
    {
        var current = CreateItem(0, "current", "Current");
        var candidate = new QueueItemInfo(1, null, "Next", "Artist", "Album", 180_000, null);

        var selected = NextTrackLyricsPreloader.SelectNext(
            new QueueSnapshot([current, candidate], 0),
            "current");

        Assert.IsNull(selected);
    }

    [TestMethod]
    public void Prepare_UnchangedCandidate_StartsOnlyOneRequest()
    {
        var requests = 0;
        using var preloader = new NextTrackLyricsPreloader((_, _, _, _, _) =>
        {
            requests++;
            return Task.FromResult(Snapshot("line"));
        });
        var next = CreateItem(1, "next", "Next");

        preloader.Prepare(next, DefaultOptions, "token");
        preloader.Prepare(next, DefaultOptions, "token");

        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task Prepare_CandidateChanges_CancelsPreviousRequest()
    {
        var previousCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var preloader = new NextTrackLyricsPreloader(async (item, _, _, cancellationToken, _) =>
        {
            if (item.Id == "first")
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    previousCanceled.TrySetResult();
                    throw;
                }
            }

            return Snapshot(item.Name);
        });

        preloader.Prepare(CreateItem(1, "first", "First"), DefaultOptions, null);
        preloader.Prepare(CreateItem(2, "second", "Second"), DefaultOptions, null);

        await previousCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task Prepare_LyricsOptionsChange_CancelsAndRestartsRequest()
    {
        var requests = 0;
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preloader = new NextTrackLyricsPreloader(async (_, _, _, cancellationToken, _) =>
        {
            requests++;
            if (requests == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    throw;
                }
            }

            return Snapshot("line");
        });
        var next = CreateItem(1, "next", "Next");

        preloader.Prepare(next, DefaultOptions, null);
        preloader.Prepare(next, new LyricsResolveOptions(LyricsSource.Netease, true), null);

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task TakeAsync_MatchingInFlightRequest_ReusesResult()
    {
        var requests = 0;
        var result = new TaskCompletionSource<LyricsSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var preloader = new NextTrackLyricsPreloader((_, _, _, _, _) =>
        {
            requests++;
            return result.Task;
        });
        var next = CreateItem(1, "next", "Next");
        preloader.Prepare(next, DefaultOptions, "token");

        var takeTask = preloader.TakeAsync(
            CreateNowPlaying("next", hasTimeSyncedLyrics: true),
            DefaultOptions,
            "token",
            CancellationToken.None);
        result.SetResult(new LyricsSnapshot(
            [new LyricLineInfo(0, 1, "line")],
            false,
            LyricsSource.Cider));

        var lyrics = await takeTask;

        Assert.AreEqual(1, requests);
        Assert.IsNotNull(lyrics);
        Assert.IsTrue(lyrics.IsTimeSynced);
    }

    [TestMethod]
    public async Task TakeAsync_PreparingFollowingTrackWhileLoading_DoesNotCancelCurrentLyrics()
    {
        var result = new TaskCompletionSource<LyricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken currentToken = default;
        using var preloader = new NextTrackLyricsPreloader((item, _, _, token, _) =>
        {
            if (item.Id == "next")
            {
                currentToken = token;
                return result.Task.WaitAsync(token);
            }

            return Task.FromResult(Snapshot("following"));
        });
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);

        var takeTask = preloader.TakeAsync(CreateNowPlaying("next"), DefaultOptions, null, CancellationToken.None);
        preloader.Prepare(CreateItem(2, "following", "Following"), DefaultOptions, null);

        Assert.IsFalse(currentToken.IsCancellationRequested, "提前预加载下一首取消了当前歌曲仍在加载的歌词。");
        result.SetResult(Snapshot("current"));
        Assert.AreEqual("current", (await takeTask)!.Lines[0].Text);
        Assert.AreEqual("following", (await preloader.TakeAsync(
            CreateNowPlaying("following"), DefaultOptions, null, CancellationToken.None))!.Lines[0].Text);
    }

    [TestMethod]
    public async Task TakeAsync_CallerCancels_CancelsTakenLoadAndPreservesFollowingTrack()
    {
        CancellationToken takenToken = default;
        using var preloader = new NextTrackLyricsPreloader((item, _, _, token, _) =>
        {
            if (item.Id == "next")
            {
                takenToken = token;
                return Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(
                    _ => LyricsSnapshot.Empty, TaskScheduler.Default);
            }

            return Task.FromResult(Snapshot("following"));
        });
        using var cancellation = new CancellationTokenSource();
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);
        var taken = preloader.TakeAsync(CreateNowPlaying("next"), DefaultOptions, null, cancellation.Token);
        preloader.Prepare(CreateItem(2, "following", "Following"), DefaultOptions, null);

        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await taken);
        Assert.IsTrue(takenToken.IsCancellationRequested);
        Assert.AreEqual("following", (await preloader.TakeAsync(
            CreateNowPlaying("following"), DefaultOptions, null, CancellationToken.None))!.Lines[0].Text);
    }

    [TestMethod]
    public async Task Dispose_TakenLoadInFlight_CancelsLoad()
    {
        CancellationToken takenToken = default;
        using var preloader = new NextTrackLyricsPreloader(async (_, _, _, token, _) =>
        {
            takenToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LyricsSnapshot.Empty;
        });
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);
        var taken = preloader.TakeAsync(CreateNowPlaying("next"), DefaultOptions, null, CancellationToken.None);

        preloader.Dispose();

        Assert.IsTrue(takenToken.IsCancellationRequested);
        Assert.IsNull(await taken.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task TakeAsync_DifferentTrack_CancelsCandidateAndReturnsNull()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preloader = new NextTrackLyricsPreloader(async (_, _, _, cancellationToken, _) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }

            return LyricsSnapshot.Empty;
        });
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);

        var lyrics = await preloader.TakeAsync(
            CreateNowPlaying("other"),
            DefaultOptions,
            null,
            CancellationToken.None);

        Assert.IsNull(lyrics);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task TakeAsync_EmptyPrefetch_ReturnsNullForForegroundRetry()
    {
        using var preloader = new NextTrackLyricsPreloader(
            (_, _, _, _, _) => Task.FromResult(LyricsSnapshot.Empty));
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);

        var lyrics = await preloader.TakeAsync(
            CreateNowPlaying("next"),
            DefaultOptions,
            null,
            CancellationToken.None);

        Assert.IsNull(lyrics);
    }

    [TestMethod]
    public async Task TakeAsync_InFlightTranslation_ReportsOriginalAndLaterUpgrade()
    {
        var completion = new TaskCompletionSource<LyricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<LyricsSnapshot>? report = null;
        var original = Snapshot("original");
        using var preloader = new NextTrackLyricsPreloader((_, _, _, _, onProgress) =>
        {
            report = onProgress;
            onProgress?.Invoke(original);
            return completion.Task;
        });
        var options = new LyricsResolveOptions(LyricsSource.Auto, true);
        preloader.Prepare(CreateItem(1, "next", "Next"), options, null);
        var updates = new List<LyricsSnapshot>();

        var taking = preloader.TakeAsync(CreateNowPlaying("next"), options, null, CancellationToken.None, updates.Add);

        Assert.IsFalse(taking.IsCompleted);
        Assert.AreSame(original, updates.Single());
        var translated = original with { Lines = [new(1, null, "original", "译文")] };
        report!(translated);
        completion.SetResult(translated);
        Assert.AreSame(translated, await taking);
        CollectionAssert.AreEqual(new[] { original, translated }, updates);
    }

    [TestMethod]
    public async Task TakeAsync_PrefetchProgressForCider_UsesCurrentTimingFlag()
    {
        var completion = new TaskCompletionSource<LyricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new LyricsSnapshot([new(1, null, "Cider")], false, LyricsSource.Cider);
        using var preloader = new NextTrackLyricsPreloader((_, _, _, _, onProgress) =>
        {
            onProgress?.Invoke(original);
            return completion.Task;
        });
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);
        var updates = new List<LyricsSnapshot>();

        var taking = preloader.TakeAsync(
            CreateNowPlaying("next", hasTimeSyncedLyrics: true), DefaultOptions, null, CancellationToken.None, updates.Add);

        Assert.IsTrue(updates.Single().IsTimeSynced);
        completion.SetResult(original);
        Assert.IsTrue((await taking)!.IsTimeSynced);
    }

    [TestMethod]
    public async Task TakeAsync_CanceledProgress_DoesNotUpdateCurrentOrFollowingTrack()
    {
        var completion = new TaskCompletionSource<LyricsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<LyricsSnapshot>? reportCurrent = null;
        using var preloader = new NextTrackLyricsPreloader((item, _, _, _, onProgress) =>
        {
            if (item.Id == "next")
            {
                reportCurrent = onProgress;
                return completion.Task;
            }

            onProgress?.Invoke(Snapshot("following"));
            return Task.FromResult(Snapshot("following"));
        });
        using var cancellation = new CancellationTokenSource();
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);
        var updates = new List<LyricsSnapshot>();
        var taking = preloader.TakeAsync(CreateNowPlaying("next"), DefaultOptions, null, cancellation.Token, updates.Add);
        preloader.Prepare(CreateItem(2, "following", "Following"), DefaultOptions, null);

        cancellation.Cancel();
        reportCurrent!(Snapshot("stale"));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await taking);
        reportCurrent(Snapshot("still stale"));
        completion.SetResult(Snapshot("stale"));

        Assert.AreEqual(0, updates.Count);
        Assert.AreEqual("following", (await preloader.TakeAsync(
            CreateNowPlaying("following"), DefaultOptions, null, CancellationToken.None))!.Lines[0].Text);
    }

    private static QueueItemInfo CreateItem(int index, string id, string name) =>
        new(index, id, name, "Artist", "Album", 180_000, "https://example.test/cover.jpg", true, true);

    private static NowPlayingInfo CreateNowPlaying(string id, bool hasTimeSyncedLyrics = false) => new()
    {
        Name = "Song",
        ArtistName = "Artist",
        AlbumName = "Album",
        PlayParameters = new PlayParameters { Id = id, Kind = "song" },
        HasLyrics = true,
        HasTimeSyncedLyrics = hasTimeSyncedLyrics
    };

    private static LyricsSnapshot Snapshot(string text) => new(
        [new LyricLineInfo(0, 1, text)],
        true,
        LyricsSource.Lrclib);
}
