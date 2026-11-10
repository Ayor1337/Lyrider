using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class LyricsDiskCacheTests
{
    private string _directory = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Lyrider.Tests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void DeleteDirectory()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveAsync_ThenTryLoadAsync_RoundTripsSnapshot()
    {
        var cache = new LyricsDiskCache(_directory);
        var snapshot = new LyricsSnapshot(
            [new(1.5, 3, "Hello world", "你好世界", [new(1.5, 2, "Hello"), new(2, 3, "world")])],
            true,
            LyricsSource.QqMusic);

        await cache.SaveAsync("song|key", snapshot);
        var loaded = await cache.TryLoadAsync("song|key");

        Assert.IsNotNull(loaded);
        Assert.AreEqual(LyricsSource.QqMusic, loaded.Source);
        Assert.IsTrue(loaded.IsTimeSynced);
        Assert.AreEqual("你好世界", loaded.Lines[0].Translation);
        Assert.AreEqual(2, loaded.Lines[0].Words![1].StartTime);
        Assert.IsNull(await cache.TryLoadAsync("other|key"));
    }

    [TestMethod]
    public async Task TryLoadAsync_CorruptFile_ReturnsNull()
    {
        var cache = new LyricsDiskCache(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, LyricsDiskCache.FileNameFor("key")), "{ not json");

        Assert.IsNull(await cache.TryLoadAsync("key"));
    }

    [TestMethod]
    public async Task ClearAsync_UnrelatedFilesInFolder_DeletesOnlyCacheFiles()
    {
        var cache = new LyricsDiskCache(_directory);
        await cache.SaveAsync("key", Synced(LyricsSource.Netease));
        var unrelated = Path.Combine(_directory, "notes.json");
        await File.WriteAllTextAsync(unrelated, "{}");
        Assert.IsTrue(await cache.GetSizeAsync() > 0);

        Assert.AreEqual(0, await cache.ClearAsync());

        Assert.AreEqual(0, await cache.GetSizeAsync());
        Assert.IsNull(await cache.TryLoadAsync("key"));
        Assert.IsTrue(File.Exists(unrelated));
    }

    [TestMethod]
    public async Task ResolveAsync_SatisfactoryResultInDiskCache_SkipsProvidersAfterRestart()
    {
        var options = new LyricsResolveOptions(LyricsSource.Auto, false);
        var first = new CountingProvider(Synced(LyricsSource.Netease));
        using (var service = new LyricsService([first]) { DiskCache = new LyricsDiskCache(_directory) })
        {
            await service.ResolveAsync(Track(), [], options);
        }

        var second = new CountingProvider(Synced(LyricsSource.Netease));
        using var restarted = new LyricsService([second]) { DiskCache = new LyricsDiskCache(_directory) };
        var result = await restarted.ResolveAsync(Track(), [], options);

        Assert.AreEqual(1, first.RequestCount);
        Assert.AreEqual(0, second.RequestCount);
        Assert.AreEqual(LyricsSource.Netease, result.Source);
        Assert.AreEqual("Line", result.Lines[0].Text);
    }

    [TestMethod]
    public async Task ResolveAsync_UnsyncedResult_IsNotWrittenToDisk()
    {
        var cache = new LyricsDiskCache(_directory);
        var provider = new CountingProvider(new([new(0, null, "Line")], false, LyricsSource.Netease));
        using var service = new LyricsService([provider]) { DiskCache = cache };

        await service.ResolveAsync(Track(), [], new(LyricsSource.Auto, false));

        Assert.AreEqual(0, await cache.GetSizeAsync());
    }

    [TestMethod]
    public async Task ClearCacheAsync_AfterResolve_QueriesProvidersAgain()
    {
        var options = new LyricsResolveOptions(LyricsSource.Auto, false);
        var provider = new CountingProvider(Synced(LyricsSource.Netease));
        using var service = new LyricsService([provider]) { DiskCache = new LyricsDiskCache(_directory) };

        await service.ResolveAsync(Track(), [], options);
        await service.ClearCacheAsync();
        await service.ResolveAsync(Track(), [], options);

        Assert.AreEqual(2, provider.RequestCount);
    }

    private static LyricsSnapshot Synced(LyricsSource source) => new([new(1, 2, "Line")], true, source);

    private static NowPlayingInfo Track() => new()
    {
        Name = "Test Song",
        ArtistName = "Test Artist",
        AlbumName = "Test Album",
        DurationInMillis = 120_000,
        HasTimeSyncedLyrics = true
    };

    private sealed class CountingProvider(LyricsSnapshot result) : ILyricsProvider
    {
        public LyricsSource Source => result.Source;

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
}
