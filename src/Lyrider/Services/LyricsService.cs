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

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient? _httpClient;
    private readonly IReadOnlyDictionary<LyricsSource, ILyricsProvider> _providers;
    private readonly ILyricsTrackResolver? _trackResolver;
    private readonly LyricsResultCache _cache;

    public LyricsService(HttpMessageHandler? handler = null)
    {
        _cache = new LyricsResultCache(TimeProvider.System);
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
        ILyricsTrackResolver? trackResolver = null,
        TimeProvider? timeProvider = null)
    {
        _providers = providers.ToDictionary(provider => provider.Source);
        _trackResolver = trackResolver;
        _cache = new LyricsResultCache(timeProvider ?? TimeProvider.System);
    }

    public async Task<LyricsSnapshot> ResolveAsync(
        NowPlayingInfo track,
        IReadOnlyList<LyricLineInfo> ciderLyrics,
        LyricsResolveOptions options,
        CancellationToken cancellationToken = default,
        Action<LyricsSnapshot>? onProgress = null)
    {
        var cacheKey = LyricsResultCache.Key(track, ciderLyrics, options);
        if (_cache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        var diskCache = DiskCache;
        if (diskCache is not null && await diskCache.TryLoadAsync(cacheKey, cancellationToken) is { } stored)
        {
            _cache.Store(cacheKey, stored, satisfactory: true);
            return stored;
        }

        var result = await ResolveUncachedAsync(track, ciderLyrics, options, cancellationToken, onProgress);
        var satisfactory = LyricsSelection.IsSatisfactory(result, options.IncludeTranslation);
        _cache.Store(cacheKey, result, satisfactory);
        if (satisfactory && diskCache is not null)
        {
            // 单个小文件，比网络查询快几个数量级；失败也只是少一条缓存。
            await diskCache.SaveAsync(cacheKey, result);
        }

        return result;
    }

    /// <summary>满意结果的持久化位置；为空时只用内存缓存。</summary>
    public LyricsDiskCache? DiskCache { get; set; }

    /// <summary>清空内存与磁盘缓存，返回没能删掉的磁盘文件数。</summary>
    public async Task<int> ClearCacheAsync()
    {
        _cache.Clear();
        return DiskCache is { } diskCache ? await diskCache.ClearAsync() : 0;
    }

    private async Task<LyricsSnapshot> ResolveUncachedAsync(
        NowPlayingInfo track,
        IReadOnlyList<LyricLineInfo> ciderLyrics,
        LyricsResolveOptions options,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onProgress)
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

        // 外部源与 Cider 歌词内容明显不符时视为错歌，不参与选优，也不借用它的翻译。
        bool AgreesWithCider(LyricsSnapshot snapshot) =>
            snapshot.Source == LyricsSource.Cider ||
            ciderSnapshot is null ||
            !LyricsMatching.ContradictsReference(ciderSnapshot.Lines, snapshot.Lines);

        bool IsAcceptedAndSatisfactory(LyricsSnapshot snapshot) =>
            LyricsSelection.IsSatisfactory(snapshot, options.IncludeTranslation) && AgreesWithCider(snapshot);

        void ConsiderCandidate(LyricsSnapshot snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AgreesWithCider(snapshot))
            {
                return;
            }

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

            searchTracks ??= await ResolveSearchTracksAsync(track, cancellationToken);
            var tracks = searchTracks;
            var result = await FetchWithTimeoutAsync(
                token => provider.FetchAsync(
                    tracks,
                    options.IncludeTranslation,
                    options.MusixmatchApiKey,
                    token,
                    ConsiderCandidate),
                cancellationToken);
            if (result is not null)
            {
                ConsiderCandidate(result);
                if (IsAcceptedAndSatisfactory(result))
                {
                    return best!;
                }
            }

            // 标题搜索没找到、或找到的内容与 Cider 歌词对不上时（常见于 Apple Music 英文名
            // 对应其他平台的日文名），用 Cider 歌词内容反查，单独计时，不占用标题搜索的预算。
            if (ciderSnapshot is not null &&
                provider is ILyricsContentSearchProvider contentProvider &&
                (result is not { Lines.Count: > 0 } ||
                    LyricsMatching.ContentCoverage(ciderSnapshot.Lines, result.Lines) < LyricsMatching.MinimumContentCoverage))
            {
                var byContent = await FetchWithTimeoutAsync(
                    token => contentProvider.SearchByLyricsAsync(
                        tracks,
                        ciderSnapshot.Lines,
                        options.IncludeTranslation,
                        token,
                        ConsiderCandidate),
                    cancellationToken);
                if (byContent is not null)
                {
                    ConsiderCandidate(byContent);
                    if (IsAcceptedAndSatisfactory(byContent))
                    {
                        return best!;
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return best ?? LyricsSnapshot.Empty;
    }

    /// <summary>
    /// 单次远程查询限时 4 秒；超时或失败返回 null 以便回退到下一步，已上报的候选仍然保留。
    /// 调用方取消照常抛出。
    /// </summary>
    private static async Task<LyricsSnapshot?> FetchWithTimeoutAsync(
        Func<CancellationToken, Task<LyricsSnapshot>> fetch,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProviderTimeout);
        try
        {
            return await fetch(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
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

/// <summary>
/// 解析结果的内存缓存，避免重播、上一首或单曲循环时整套重查。
/// 满意的结果在本次运行内一直有效；不满意或空的结果可能来自超时或限流，只保留 5 分钟。
/// </summary>
internal sealed class LyricsResultCache(TimeProvider timeProvider)
{
    private const int Capacity = 64;
    private static readonly TimeSpan UnsatisfactoryLifetime = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _sequence;

    // 结果取决于歌曲、选项以及有没有拿到 Cider 歌词（用于对齐与内容核对）；不把 API Key 放进键里。
    public static string Key(NowPlayingInfo track, IReadOnlyList<LyricLineInfo> ciderLyrics, LyricsResolveOptions options) =>
        string.Join("|",
            TrackIdentity.For(track),
            options.Source,
            options.IncludeTranslation,
            options.IncludeWordTiming,
            !string.IsNullOrWhiteSpace(options.MusixmatchApiKey),
            ciderLyrics.Count > 0);

    public bool TryGet(string key, out LyricsSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) &&
                (entry.ExpiresAt is null || timeProvider.GetUtcNow() < entry.ExpiresAt))
            {
                _entries[key] = entry with { LastUsed = ++_sequence };
                snapshot = entry.Snapshot;
                return true;
            }

            _entries.Remove(key);
            snapshot = LyricsSnapshot.Empty;
            return false;
        }
    }

    public void Store(string key, LyricsSnapshot snapshot, bool satisfactory)
    {
        lock (_lock)
        {
            DateTimeOffset? expiresAt = satisfactory ? null : timeProvider.GetUtcNow() + UnsatisfactoryLifetime;
            _entries[key] = new Entry(snapshot, expiresAt, ++_sequence);
            if (_entries.Count > Capacity)
            {
                _entries.Remove(_entries.MinBy(pair => pair.Value.LastUsed).Key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private sealed record Entry(LyricsSnapshot Snapshot, DateTimeOffset? ExpiresAt, long LastUsed);
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
