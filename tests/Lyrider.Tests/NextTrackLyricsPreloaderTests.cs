using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class NextTrackLyricsPreloaderTests
{
    private static readonly LyricsResolveOptions DefaultOptions = new(LyricsSource.Auto, false);

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
        using var preloader = new NextTrackLyricsPreloader((_, _, _, _) =>
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
        using var preloader = new NextTrackLyricsPreloader(async (item, _, _, cancellationToken) =>
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
        using var preloader = new NextTrackLyricsPreloader(async (_, _, _, cancellationToken) =>
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
        using var preloader = new NextTrackLyricsPreloader((_, _, _, _) =>
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
    public async Task TakeAsync_DifferentTrack_CancelsCandidateAndReturnsNull()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preloader = new NextTrackLyricsPreloader(async (_, _, _, cancellationToken) =>
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
            (_, _, _, _) => Task.FromResult(LyricsSnapshot.Empty));
        preloader.Prepare(CreateItem(1, "next", "Next"), DefaultOptions, null);

        var lyrics = await preloader.TakeAsync(
            CreateNowPlaying("next"),
            DefaultOptions,
            null,
            CancellationToken.None);

        Assert.IsNull(lyrics);
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
