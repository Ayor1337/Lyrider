using Lyrider.Models;

namespace Lyrider.Services;

internal sealed class NextTrackLyricsPreloader : IDisposable
{
    private static readonly TimeSpan CiderPreloadTimeout = TimeSpan.FromSeconds(12);

    private readonly Func<QueueItemInfo, LyricsResolveOptions, string?, CancellationToken, Task<LyricsSnapshot>> _load;

    private CancellationTokenSource? _preloadCancellation;
    private Task<LyricsSnapshot>? _preloadTask;
    private string? _trackKey;
    private LyricsResolveOptions? _options;
    private string? _appToken;
    private bool _disposed;

    public NextTrackLyricsPreloader(CiderService ciderService, LyricsService lyricsService)
        : this(async (item, options, appToken, cancellationToken) =>
        {
            var track = ToNowPlayingInfo(item);
            var ciderLyrics = await ciderService.GetLyricsAsync(
                item.Id,
                appToken,
                cancellationToken,
                CiderPreloadTimeout);
            return await lyricsService.ResolveAsync(track, ciderLyrics, options, cancellationToken);
        })
    {
    }

    internal NextTrackLyricsPreloader(
        Func<QueueItemInfo, LyricsResolveOptions, string?, CancellationToken, Task<LyricsSnapshot>> load)
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
        _preloadTask = LoadSafelyAsync(item, options, appToken, _preloadCancellation.Token);
    }

    public async Task<LyricsSnapshot?> TakeAsync(
        NowPlayingInfo track,
        LyricsResolveOptions options,
        string? appToken,
        CancellationToken cancellationToken)
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

        var result = await task.WaitAsync(cancellationToken);
        Release(task);
        if (result.Lines.Count == 0)
        {
            return null;
        }

        return result.Source == LyricsSource.Cider
            ? result with { IsTimeSynced = track.HasTimeSyncedLyrics }
            : result;
    }

    public void Clear()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = null;
        _preloadTask = null;
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

    private async Task<LyricsSnapshot> LoadSafelyAsync(
        QueueItemInfo item,
        LyricsResolveOptions options,
        string? appToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _load(item, options, appToken, cancellationToken);
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

    private void Release(Task<LyricsSnapshot> task)
    {
        if (!ReferenceEquals(_preloadTask, task))
        {
            return;
        }

        _preloadCancellation?.Dispose();
        _preloadCancellation = null;
        _preloadTask = null;
        _trackKey = null;
        _options = null;
        _appToken = null;
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
