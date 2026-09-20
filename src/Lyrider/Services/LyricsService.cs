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
        var ciderSnapshot = ciderLyrics.Count > 0
            ? PrepareSnapshot(
                new LyricsSnapshot(ciderLyrics, track.HasTimeSyncedLyrics, LyricsSource.Cider),
                null)
            : null;

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
                if (ciderSnapshot is not null)
                {
                    if (!lookForTranslation || !NeedsTranslation(ciderSnapshot))
                    {
                        return ciderSnapshot;
                    }

                    fallback = ciderSnapshot;
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
                var result = PrepareSnapshot(
                    await provider.FetchAsync(
                        searchTracks,
                        options.IncludeTranslation,
                        options.MusixmatchApiKey,
                        providerTimeout.Token),
                    ciderSnapshot);
                if (result.Lines.Count > 0)
                {
                    if (!lookForTranslation || !NeedsTranslation(result))
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

    private static bool NeedsTranslation(LyricsSnapshot snapshot) =>
        !IsPrimarilyChinese(snapshot) &&
        !snapshot.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Translation));

    private static bool IsPrimarilyChinese(LyricsSnapshot snapshot)
    {
        var textLines = snapshot.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
        return textLines.Length > 0 &&
            textLines.Count(line => LyricsParsing.IsChineseTranslation(line.Text)) * 2 >= textLines.Length;
    }

    private static LyricsSnapshot PrepareSnapshot(
        LyricsSnapshot snapshot,
        LyricsSnapshot? ciderSnapshot)
    {
        IReadOnlyList<LyricLineInfo> lines = IsPrimarilyChinese(snapshot)
            ? snapshot.Lines.Select(line => line with { Translation = null }).ToArray()
            : snapshot.Lines;
        var prepared = snapshot with { Lines = lines };
        return AlignToCiderTiming(prepared, ciderSnapshot);
    }

    private static LyricsSnapshot AlignToCiderTiming(
        LyricsSnapshot snapshot,
        LyricsSnapshot? ciderSnapshot)
    {
        if (snapshot.Source == LyricsSource.Cider ||
            !snapshot.IsTimeSynced ||
            ciderSnapshot?.IsTimeSynced != true)
        {
            return snapshot;
        }

        var ciderTimes = ciderSnapshot.Lines
            .GroupBy(line => LyricsMatching.Normalize(line.Text))
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().StartTime);
        var offsets = snapshot.Lines
            .GroupBy(line => LyricsMatching.Normalize(line.Text))
            .Where(group => group.Key.Length > 0 && group.Count() == 1 && ciderTimes.ContainsKey(group.Key))
            .Select(group => group.Single().StartTime - ciderTimes[group.Key])
            .OrderBy(offset => offset)
            .ToArray();
        if (offsets.Length < 3)
        {
            return snapshot;
        }

        var middle = offsets.Length / 2;
        var offset = offsets.Length % 2 == 0
            ? (offsets[middle - 1] + offsets[middle]) / 2
            : offsets[middle];
        var alignedLines = snapshot.Lines.Select(line =>
        {
            var startTime = Math.Max(0, line.StartTime - offset);
            double? endTime = line.EndTime is { } end
                ? Math.Max(startTime, end - offset)
                : null;
            return line with { StartTime = startTime, EndTime = endTime };
        }).ToArray();
        return snapshot with { Lines = alignedLines };
    }

    public static LyricsSource ParseSource(string? value) =>
        Enum.TryParse<LyricsSource>(value, true, out var source) && Enum.IsDefined(source)
            ? source
            : LyricsSource.Auto;

    public static bool SupportsTranslation(LyricsSource source) =>
        source != LyricsSource.Cider;

    public void Dispose() => _httpClient?.Dispose();
}
