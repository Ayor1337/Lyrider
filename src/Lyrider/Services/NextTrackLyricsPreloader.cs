using Lyrider.Models;

namespace Lyrider.Services;

internal sealed class NextTrackLyricsPreloader : IDisposable
{
    private static readonly TimeSpan CiderPreloadTimeout = TimeSpan.FromSeconds(12);

    private readonly Func<QueueItemInfo, LyricsResolveOptions, string?, CancellationToken, Action<LyricsSnapshot>?, Task<LyricsSnapshot>> _load;
    private readonly HashSet<CancellationTokenSource> _activeLoads = [];

    private CancellationTokenSource? _preloadCancellation;
    private Task<LyricsSnapshot>? _preloadTask;
    private PreloadProgress? _preloadProgress;
    private string? _trackKey;
    private LyricsResolveOptions? _options;
    private string? _appToken;
    private bool _disposed;

    public NextTrackLyricsPreloader(CiderService ciderService, LyricsService lyricsService)
        : this(async (item, options, appToken, cancellationToken, onProgress) =>
        {
            var track = ToNowPlayingInfo(item);
            var ciderLyrics = await ciderService.GetLyricsAsync(
                item.Id,
                appToken,
                cancellationToken,
                CiderPreloadTimeout,
                options.IncludeWordTiming);
            return await lyricsService.ResolveAsync(track, ciderLyrics, options, cancellationToken, onProgress);
        })
    {
    }

    internal NextTrackLyricsPreloader(
        Func<QueueItemInfo, LyricsResolveOptions, string?, CancellationToken, Action<LyricsSnapshot>?, Task<LyricsSnapshot>> load)
    {
        _load = load;
    }

    public void Prepare(QueueItemInfo? item, LyricsResolveOptions options, string? appToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (item is null || string.IsNullOrWhiteSpace(item.Id))
        {
            Clear();
            return;
        }

        var trackKey = TrackIdentity.For(item);
        if (string.Equals(_trackKey, trackKey, StringComparison.Ordinal) &&
            Equals(_options, options) &&
            string.Equals(_appToken, appToken, StringComparison.Ordinal))
        {
            return;
        }

        Clear();
        _trackKey = trackKey;
        _options = options;
        _appToken = appToken;
        _preloadCancellation = new CancellationTokenSource();
        var progress = new PreloadProgress();
        _preloadProgress = progress;
        _preloadTask = LoadSafelyAsync(item, options, appToken, _preloadCancellation.Token, progress.Report);
    }

    public async Task<LyricsSnapshot?> TakeAsync(
        NowPlayingInfo track,
        LyricsResolveOptions options,
        string? appToken,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onProgress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var task = _preloadTask;
        if (task is null ||
            !string.Equals(_trackKey, TrackIdentity.For(track), StringComparison.Ordinal) ||
            !Equals(_options, options) ||
            !string.Equals(_appToken, appToken, StringComparison.Ordinal))
        {
            Clear();
            return null;
        }

        // 当前歌曲接手请求后，队列可以立即准备下一首，不再取消这次加载。
        var loadCancellation = _preloadCancellation!;
        var progress = _preloadProgress!;
        _preloadCancellation = null;
        _preloadTask = null;
        _preloadProgress = null;
        _trackKey = null;
        _options = null;
        _appToken = null;
        _activeLoads.Add(loadCancellation);
        using var registration = cancellationToken.Register(loadCancellation.Cancel);
        try
        {
            LyricsSnapshot ForCurrentTrack(LyricsSnapshot snapshot) => snapshot.Source == LyricsSource.Cider
                ? snapshot with { IsTimeSynced = track.HasTimeSyncedLyrics }
                : snapshot;

            progress.OnProgress = snapshot =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    onProgress?.Invoke(ForCurrentTrack(snapshot));
                }
            };
            if (progress.Latest is { } latest)
            {
                progress.OnProgress(latest);
            }

            var result = await task.WaitAsync(cancellationToken);
            if (result.Lines.Count == 0)
            {
                return null;
            }

            return ForCurrentTrack(result);
        }
        finally
        {
            progress.OnProgress = null;
            _activeLoads.Remove(loadCancellation);
            registration.Dispose();
            loadCancellation.Cancel();
            loadCancellation.Dispose();
        }
    }

    public void Clear()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = null;
        _preloadTask = null;
        _preloadProgress = null;
        _trackKey = null;
        _options = null;
        _appToken = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
        foreach (var cancellation in _activeLoads.ToArray())
        {
            cancellation.Cancel();
        }
    }

    internal static QueueItemInfo? SelectNext(QueueSnapshot snapshot, string? currentTrackId)
    {
        if (snapshot.CurrentIndex < 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(currentTrackId) &&
            snapshot.Items.Count(item => string.Equals(
                item.Id,
                currentTrackId,
                StringComparison.Ordinal)) > 1)
        {
            return null;
        }

        return snapshot.Items.FirstOrDefault(item =>
            item.Index > snapshot.CurrentIndex &&
            !string.IsNullOrWhiteSpace(item.Id) &&
            !string.IsNullOrWhiteSpace(item.Name));
    }

    private sealed class PreloadProgress
    {
        public LyricsSnapshot? Latest { get; private set; }

        public Action<LyricsSnapshot>? OnProgress { get; set; }

        public void Report(LyricsSnapshot snapshot)
        {
            Latest = snapshot;
            OnProgress?.Invoke(snapshot);
        }
    }

    private async Task<LyricsSnapshot> LoadSafelyAsync(
        QueueItemInfo item,
        LyricsResolveOptions options,
        string? appToken,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onProgress)
    {
        try
        {
            return await _load(item, options, appToken, cancellationToken, onProgress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LyricsSnapshot.Empty;
        }
        catch (Exception)
        {
            return LyricsSnapshot.Empty;
        }
    }

    private static NowPlayingInfo ToNowPlayingInfo(QueueItemInfo item) => new()
    {
        Name = item.Name,
        ArtistName = item.ArtistName,
        AlbumName = item.AlbumName,
        DurationInMillis = item.DurationInMillis,
        Artwork = new ArtworkInfo { Url = item.ArtworkUrl },
        PlayParameters = new PlayParameters { Id = item.Id, Kind = "song" },
        HasLyrics = item.HasLyrics,
        HasTimeSyncedLyrics = item.HasTimeSyncedLyrics
    };
}

internal static class TrackIdentity
{
    public static string For(NowPlayingInfo track) =>
        Identity(track.PlayParameters?.Id, track.Name, track.ArtistName, track.AlbumName);

    public static string For(QueueItemInfo track) =>
        Identity(track.Id, track.Name, track.ArtistName, track.AlbumName);

    private static string Identity(string? id, string? name, string? artistName, string? albumName) =>
        !string.IsNullOrWhiteSpace(id)
            ? id
            : $"{name}\u001F{artistName}\u001F{albumName}";
}
