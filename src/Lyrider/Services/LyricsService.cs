using Lyrider.Models;

namespace Lyrider.Services;

public sealed record LyricsResolveOptions(
    LyricsSource Source,
    bool IncludeTranslation,
    string? MusixmatchApiKey = null);

public sealed class LyricsService : IDisposable
{
    private static readonly LyricsSource[] AutomaticOrder =
    [
        LyricsSource.Cider,
        LyricsSource.Netease,
        LyricsSource.QqMusic,
        LyricsSource.Musixmatch,
        LyricsSource.Lrclib
    ];

    private readonly HttpClient? _httpClient;
    private readonly IReadOnlyDictionary<LyricsSource, ILyricsProvider> _providers;
    private readonly ILyricsTrackResolver? _trackResolver;

    public LyricsService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Lyrider/1.0 (+https://github.com/Ayor1337/Lyrider)");

        _providers = new ILyricsProvider[]
        {
            new NeteaseLyricsProvider(_httpClient),
            new QqMusicLyricsProvider(_httpClient),
            new MusixmatchLyricsProvider(_httpClient),
            new LrclibLyricsProvider(_httpClient)
        }.ToDictionary(provider => provider.Source);
        _trackResolver = new AppleMusicTrackResolver(_httpClient);
    }

    internal LyricsService(
        IEnumerable<ILyricsProvider> providers,
        ILyricsTrackResolver? trackResolver = null)
    {
        _providers = providers.ToDictionary(provider => provider.Source);
        _trackResolver = trackResolver;
    }

    public async Task<LyricsSnapshot> ResolveAsync(
        NowPlayingInfo track,
        IReadOnlyList<LyricLineInfo> ciderLyrics,
        LyricsResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        var requestedSource = Enum.IsDefined(options.Source) ? options.Source : LyricsSource.Auto;
        var sources = requestedSource == LyricsSource.Auto
            ? AutomaticOrder
            : [requestedSource];
        var lookForTranslation = requestedSource == LyricsSource.Auto && options.IncludeTranslation;
        LyricsSnapshot? fallback = null;
        IReadOnlyList<LyricsSearchTrack>? searchTracks = null;

        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(TimeSpan.FromSeconds(12));

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalTimeout.IsCancellationRequested)
            {
                break;
            }

            if (source == LyricsSource.Cider)
            {
                if (ciderLyrics.Count > 0)
                {
                    var cider = new LyricsSnapshot(
                        ciderLyrics,
                        track.HasTimeSyncedLyrics,
                        LyricsSource.Cider);
                    if (!lookForTranslation || HasTranslation(cider))
                    {
                        return cider;
                    }

                    fallback = cider;
                }

                continue;
            }

            if (source == LyricsSource.Musixmatch && string.IsNullOrWhiteSpace(options.MusixmatchApiKey))
            {
                continue;
            }

            if (!_providers.TryGetValue(source, out var provider))
            {
                continue;
            }

            try
            {
                searchTracks ??= await ResolveSearchTracksAsync(track, totalTimeout.Token);
                using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
                providerTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                var result = await provider.FetchAsync(
                    searchTracks,
                    options.IncludeTranslation,
                    options.MusixmatchApiKey,
                    providerTimeout.Token);
                if (result.Lines.Count > 0)
                {
                    if (!lookForTranslation || HasTranslation(result))
                    {
                        return result;
                    }

                    fallback ??= result;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (totalTimeout.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A remote provider must never break playback or prevent the next fallback.
            }
        }

        return fallback ?? LyricsSnapshot.Empty;
    }

    private async Task<IReadOnlyList<LyricsSearchTrack>> ResolveSearchTracksAsync(
        NowPlayingInfo track,
        CancellationToken cancellationToken)
    {
        if (_trackResolver is null)
        {
            return [LyricsSearchTrack.From(track)];
        }

        using var resolverTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        resolverTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await _trackResolver.ResolveAsync(track, resolverTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [LyricsSearchTrack.From(track)];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [LyricsSearchTrack.From(track)];
        }
    }

    private static bool HasTranslation(LyricsSnapshot snapshot) =>
        snapshot.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Translation));

    public static LyricsSource ParseSource(string? value) =>
        Enum.TryParse<LyricsSource>(value, true, out var source) && Enum.IsDefined(source)
            ? source
            : LyricsSource.Auto;

    public void Dispose() => _httpClient?.Dispose();
}
