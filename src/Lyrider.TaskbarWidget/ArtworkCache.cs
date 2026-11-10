using System.IO;
using System.Net.Http;

namespace Lyrider.TaskbarWidget;

/// <summary>
/// 封面图片的磁盘缓存，主窗口（WinUI）和任务栏（WPF）共用一个实例，可跨线程使用。
/// 文件按 URL 命名；每次命中会刷新修改时间，超出容量时先淘汰最久没用的。
/// </summary>
public sealed class ArtworkCache
{
    public const string DirectoryName = "artwork";
    public const long DefaultCapacityBytes = 256L * 1024 * 1024;
    private const string Extension = ".img";

    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _client;
    private volatile string? _directoryPath;

    public ArtworkCache(string? directoryPath, HttpClient? client = null)
    {
        _directoryPath = directoryPath;
        _client = client ?? DefaultClient;
    }

    /// <summary>为空时只下载、不读写磁盘。设置里换了缓存位置时直接改这里。</summary>
    public string? DirectoryPath
    {
        get => _directoryPath;
        set => _directoryPath = value;
    }

    /// <summary>只查磁盘，不发请求；给需要同步拿到图片源的地方用。</summary>
    public bool TryReadCached(Uri uri, out byte[] bytes)
    {
        bytes = [];
        if (PathFor(uri) is not { } path)
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            Touch(path);
            return bytes.Length > 0;
        }
        catch (Exception)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>先查磁盘，没有再下载；下载到的图片写入缓存。网络失败照常抛出，交给调用方回退。</summary>
    public async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var path = PathFor(uri);
        if (path is not null)
        {
            try
            {
                if (File.Exists(path))
                {
                    var cached = await File.ReadAllBytesAsync(path, cancellationToken);
                    if (cached.Length > 0)
                    {
                        Touch(path);
                        return cached;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 读不了就当没缓存，下面重新下载并覆盖。
            }
        }

        using var response = await _client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // 只存图片，避免把 CDN 的错误页当封面永久留下。
        if (path is not null && bytes.Length > 0 &&
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            await CacheFiles.WriteAtomicallyAsync(path, stream => stream.WriteAsync(bytes, cancellationToken).AsTask());
        }

        return bytes;
    }

    /// <summary>缓存文件解不出图片时调用，下次重新下载。</summary>
    public void Remove(Uri uri)
    {
        if (PathFor(uri) is { } path)
        {
            CacheFiles.TryDelete(path);
        }
    }

    public Task<long> GetSizeAsync() => CacheFiles.GetSizeAsync(DirectoryPath, Extension);

    public Task<int> ClearAsync() => CacheFiles.ClearAsync(DirectoryPath, Extension);

    /// <summary>总大小超过 <paramref name="capacityBytes"/> 时，从最久没用的开始删。</summary>
    public Task TrimAsync(long capacityBytes = DefaultCapacityBytes)
    {
        var directoryPath = DirectoryPath;
        return Task.Run(() =>
        {
            long kept = 0;
            foreach (var file in CacheFiles.Enumerate(directoryPath, Extension).OrderByDescending(file => file.LastWriteTimeUtc))
            {
                kept += file.Length;
                if (kept > capacityBytes)
                {
                    CacheFiles.TryDelete(file.FullName);
                }
            }
        });
    }

    private string? PathFor(Uri uri) =>
        DirectoryPath is { Length: > 0 } directory &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? Path.Combine(directory, CacheFiles.FileName(uri.AbsoluteUri, Extension))
            : null;

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
        }
    }
}
