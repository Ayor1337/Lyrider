using System.Net;
using System.Net.Http.Headers;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class ArtworkCacheTests
{
    private static readonly Uri Cover = new("https://is1-ssl.mzstatic.com/image/thumb/cover/800x800bb.jpg");
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
    public async Task GetBytesAsync_SecondRequest_ReadsDiskWithoutDownloading()
    {
        var handler = new CountingHandler([1, 2, 3], "image/jpeg");
        var cache = new ArtworkCache(_directory, new HttpClient(handler));

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await cache.GetBytesAsync(Cover));
        var restarted = new ArtworkCache(_directory, new HttpClient(handler));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await restarted.GetBytesAsync(Cover));
        Assert.IsTrue(restarted.TryReadCached(Cover, out var bytes));

        Assert.AreEqual(1, handler.RequestCount);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
    }

    [TestMethod]
    public async Task GetBytesAsync_NonImageResponse_IsNotCached()
    {
        var handler = new CountingHandler([1, 2, 3], "text/html");
        var cache = new ArtworkCache(_directory, new HttpClient(handler));

        await cache.GetBytesAsync(Cover);

        Assert.IsFalse(cache.TryReadCached(Cover, out _));
        Assert.AreEqual(0, await cache.GetSizeAsync());
    }

    [TestMethod]
    public async Task GetBytesAsync_NoDirectory_DownloadsWithoutWriting()
    {
        var handler = new CountingHandler([1], "image/png");
        var cache = new ArtworkCache(null, new HttpClient(handler));

        await cache.GetBytesAsync(Cover);
        await cache.GetBytesAsync(Cover);

        Assert.AreEqual(2, handler.RequestCount);
        Assert.IsFalse(Directory.Exists(_directory));
    }

    [TestMethod]
    public async Task TrimAsync_OverCapacity_DeletesLeastRecentlyUsedFirst()
    {
        var cache = new ArtworkCache(_directory, new HttpClient(new CountingHandler(new byte[100], "image/jpeg")));
        Uri[] covers = [new("https://example.test/a.jpg"), new("https://example.test/b.jpg"), new("https://example.test/c.jpg")];
        foreach (var cover in covers)
        {
            await cache.GetBytesAsync(cover);
        }

        var now = DateTime.UtcNow;
        for (var index = 0; index < covers.Length; index++)
        {
            var path = Path.Combine(_directory, CacheFiles.FileName(covers[index].AbsoluteUri, ".img"));
            File.SetLastWriteTimeUtc(path, now.AddMinutes(index));
        }

        await cache.TrimAsync(capacityBytes: 250);

        Assert.IsFalse(cache.TryReadCached(covers[0], out _));
        Assert.IsTrue(cache.TryReadCached(covers[1], out _));
        Assert.IsTrue(cache.TryReadCached(covers[2], out _));
    }

    [TestMethod]
    public async Task ClearAsync_UnrelatedFilesInFolder_DeletesOnlyCovers()
    {
        var cache = new ArtworkCache(_directory, new HttpClient(new CountingHandler([1], "image/jpeg")));
        await cache.GetBytesAsync(Cover);
        var unrelated = Path.Combine(_directory, "photo.img");
        await File.WriteAllTextAsync(unrelated, "keep");

        Assert.AreEqual(0, await cache.ClearAsync());

        Assert.IsFalse(cache.TryReadCached(Cover, out _));
        Assert.IsTrue(File.Exists(unrelated));
    }

    private sealed class CountingHandler(byte[] body, string mediaType) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
