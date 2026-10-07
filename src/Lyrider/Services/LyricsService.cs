using Lyrider.Models;

namespace Lyrider.Services;

public sealed record LyricsResolveOptions(
    LyricsSource Source,
    bool IncludeTranslation,
    string? MusixmatchApiKey = null,
    bool IncludeWordTiming = false);

public sealed class LyricsService : IDisposable
{
    private static readonly LyricsSource[] AutomaticOrder =
    [
        LyricsSource.Netease,
        LyricsSource.QqMusic,
        LyricsSource.Cider,
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
        CancellationToken cancellationToken = default,
        Action<LyricsSnapshot>? onProgress = null)
    {
        var requestedSource = Enum.IsDefined(options.Source) ? options.Source : LyricsSource.Auto;
        var sources = requestedSource == LyricsSource.Auto
            ? AutomaticOrder
            : new[] { requestedSource }.Concat(AutomaticOrder.Where(source => source != requestedSource)).ToArray();
        LyricsSnapshot? best = null;
        IReadOnlyList<LyricsSearchTrack>? searchTracks = null;
        var ciderSnapshot = ciderLyrics.Count > 0
            ? PrepareSnapshot(
                new LyricsSnapshot(ciderLyrics, track.HasTimeSyncedLyrics, LyricsSource.Cider),
                null)
            : null;

        void ConsiderCandidate(LyricsSnapshot snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = PrepareSnapshot(snapshot, ciderSnapshot);
            if (options.IncludeWordTiming && options.IncludeTranslation && best is not null)
                prepared = MergeMatchingTranslations(prepared, best);
            if (!prepared.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)))
            {
                return;
            }

            var comparison = best is null ? 1 : LyricsSelection.Compare(prepared, best, options.IncludeTranslation);
            if (options.IncludeWordTiming && best is not null)
            {
                var candidateHasWords = prepared.IsTimeSynced && prepared.Lines.Any(line => line.Words is { Count: > 0 });
                var bestHasWords = best.IsTimeSynced && best.Lines.Any(line => line.Words is { Count: > 0 });
                if (candidateHasWords != bestHasWords) comparison = candidateHasWords ? 1 : -1;
            }
            if (comparison > 0 ||
                (comparison == 0 && Array.IndexOf(sources, prepared.Source) < Array.IndexOf(sources, best!.Source)))
            {
                best = prepared;
                onProgress?.Invoke(prepared);
            }
            if (options.IncludeWordTiming && options.IncludeTranslation && best is not null)
            {
                var translated = MergeMatchingTranslations(best, prepared);
                if (!ReferenceEquals(translated, best))
                {
                    best = translated;
                    onProgress?.Invoke(best);
                }
            }
        }

        if (ciderSnapshot is not null)
        {
            ConsiderCandidate(ciderSnapshot);
        }

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source == LyricsSource.Cider)
            {
                if (ciderSnapshot is not null && LyricsSelection.IsSatisfactory(ciderSnapshot, options.IncludeTranslation))
                {
                    return best!;
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
                searchTracks ??= await ResolveSearchTracksAsync(track, cancellationToken);
                using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                var result = await provider.FetchAsync(
                    searchTracks,
                    options.IncludeTranslation,
                    options.MusixmatchApiKey,
                    providerTimeout.Token,
                    ConsiderCandidate);
                ConsiderCandidate(result);
                if (LyricsSelection.IsSatisfactory(result, options.IncludeTranslation))
                {
                    return best!;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 该源超时后继续查下一源，已找到的候选仍然保留。
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 远程源失败后继续回退。
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return best ?? LyricsSnapshot.Empty;
    }

    private static LyricsSnapshot MergeMatchingTranslations(LyricsSnapshot target, LyricsSnapshot source)
    {
        var translated = target.Lines.Select(line =>
        {
            if (!string.IsNullOrWhiteSpace(line.Translation)) return line;
            var matches = source.Lines.Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Translation) &&
                LyricsMatching.Normalize(candidate.Text) == LyricsMatching.Normalize(line.Text) &&
                Math.Abs(candidate.StartTime - line.StartTime) <= 0.5).ToArray();
            return matches.Length == 1 ? line with { Translation = matches[0].Translation } : line;
        }).ToArray();
        return translated.SequenceEqual(target.Lines) ? target : target with { Lines = translated };
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

    private static LyricsSnapshot PrepareSnapshot(
        LyricsSnapshot snapshot,
        LyricsSnapshot? ciderSnapshot)
    {
        IReadOnlyList<LyricLineInfo> lines = LyricsSelection.IsPrimarilyChinese(snapshot)
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
            return line with
            {
                StartTime = startTime, EndTime = endTime,
                Words = line.Words?.Select(word => word with
                {
                    StartTime = Math.Max(0, word.StartTime - offset),
                    EndTime = Math.Max(0, word.EndTime - offset)
                }).ToArray()
            };
        }).ToArray();
        return snapshot with { Lines = alignedLines };
    }

    public static LyricsSource ParseSource(string? value) =>
        Enum.TryParse<LyricsSource>(value, true, out var source) && Enum.IsDefined(source)
            ? source
            : LyricsSource.Auto;

    public void Dispose() => _httpClient?.Dispose();
}

internal static class LyricsSelection
{
    private const double MinimumTranslationCoverage = 0.8;

    public static bool IsSatisfactory(LyricsSnapshot snapshot, bool includeTranslation) =>
        snapshot.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)) &&
        snapshot.IsTimeSynced &&
        (!includeTranslation || IsPrimarilyChinese(snapshot) || TranslationCoverage(snapshot) >= MinimumTranslationCoverage);

    public static int Compare(LyricsSnapshot candidate, LyricsSnapshot current, bool includeTranslation)
    {
        var satisfactory = IsSatisfactory(candidate, includeTranslation).CompareTo(IsSatisfactory(current, includeTranslation));
        if (satisfactory != 0)
        {
            return satisfactory;
        }

        if (includeTranslation)
        {
            var coverage = TranslationCoverage(candidate).CompareTo(TranslationCoverage(current));
            if (coverage != 0)
            {
                return coverage;
            }
        }

        return candidate.IsTimeSynced.CompareTo(current.IsTimeSynced);
    }

    public static double TranslationCoverage(LyricsSnapshot snapshot)
    {
        var textLines = snapshot.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
        return textLines.Length == 0
            ? 0
            : (double)textLines.Count(line => !string.IsNullOrWhiteSpace(line.Translation)) / textLines.Length;
    }

    public static bool IsPrimarilyChinese(LyricsSnapshot snapshot)
    {
        var textLines = snapshot.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
        return textLines.Length > 0 &&
            !textLines.Any(line => line.Text.Any(character => character is >= '\u3040' and <= '\u30ff' or >= '\u31f0' and <= '\u31ff')) &&
            textLines.Count(line => LyricsParsing.IsChineseTranslation(line.Text)) * 2 >= textLines.Length;
    }
}
