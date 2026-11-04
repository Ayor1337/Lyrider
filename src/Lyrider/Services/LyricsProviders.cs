using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lyrider.Models;

namespace Lyrider.Services;

internal interface ILyricsProvider
{
    LyricsSource Source { get; }

    Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null);
}

/// <summary>
/// 标题和艺人对不上时（如 Apple Music 用英文名、其他平台用日文名），
/// 用已有的参考歌词内容反查同一首歌。
/// </summary>
internal interface ILyricsContentSearchProvider
{
    Task<LyricsSnapshot> SearchByLyricsAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        IReadOnlyList<LyricLineInfo> referenceLyrics,
        bool includeTranslation,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null);
}

internal sealed record LyricsSearchTrack(
    string Name,
    string ArtistName,
    string? AlbumName,
    double DurationInMillis)
{
    public static LyricsSearchTrack From(NowPlayingInfo track) => new(
        track.Name?.Trim() ?? string.Empty,
        track.ArtistName?.Trim() ?? string.Empty,
        track.AlbumName?.Trim(),
        track.DurationInMillis);
}

internal interface ILyricsTrackResolver
{
    Task<IReadOnlyList<LyricsSearchTrack>> ResolveAsync(
        NowPlayingInfo track,
        CancellationToken cancellationToken);
}

/// <summary>
/// 为外部歌词源补充搜索别名：用 ISRC 查原文标题（Deezer，失败再查 MusicBrainz），
/// 以及用 Apple 曲目 ID 查日区商店名称。两路并行，各自限时，任一失败不影响另一路。
/// </summary>
internal sealed class AppleMusicTrackResolver(HttpClient httpClient) : ILyricsTrackResolver
{
    // 比 LyricsService 给解析器的总预算（2 秒）短，超时的一路被丢弃，已完成的一路仍然可用。
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);

    public async Task<IReadOnlyList<LyricsSearchTrack>> ResolveAsync(
        NowPlayingInfo track,
        CancellationToken cancellationToken)
    {
        var original = LyricsSearchTrack.From(track);
        var aliases = await Task.WhenAll(
            LookupSafelyAsync(token => ResolveIsrcAsync(track.Isrc, token), cancellationToken),
            LookupSafelyAsync(token => ResolveStorefrontAsync(track, token), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        var tracks = new List<LyricsSearchTrack> { original };
        foreach (var alias in aliases)
        {
            if (alias is not null && !tracks.Any(existing => LyricsMatching.IsSameIdentity(existing, alias)))
            {
                tracks.Add(alias);
            }
        }

        return tracks;
    }

    private static async Task<LyricsSearchTrack?> LookupSafelyAsync(
        Func<CancellationToken, Task<LyricsSearchTrack?>> lookup,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            return await lookup(timeout.Token);
        }
        catch (Exception)
        {
            // 调用方取消由 ResolveAsync 统一抛出；超时与远程错误只表示少一个别名。
            return null;
        }
    }

    private async Task<LyricsSearchTrack?> ResolveIsrcAsync(string? isrc, CancellationToken cancellationToken)
    {
        isrc = isrc?.Trim().ToUpperInvariant();
        if (!IsValidIsrc(isrc))
        {
            return null;
        }

        return await ResolveDeezerAsync(isrc!, cancellationToken) ??
            await ResolveMusicBrainzAsync(isrc!, cancellationToken);
    }

    private async Task<LyricsSearchTrack?> ResolveDeezerAsync(string isrc, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://api.deezer.com/track/isrc:{isrc}", cancellationToken);
        if (document is null)
        {
            return null;
        }

        // 查不到时 Deezer 仍回 200，body 为 {"error":{...}}。
        var root = document.RootElement;
        var name = LyricsParsing.GetString(root, "title_short") ?? LyricsParsing.GetString(root, "title");
        var artist = LyricsParsing.TryGetPath(root, out var artistElement, "artist")
            ? LyricsParsing.GetString(artistElement, "name")
            : null;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        return new LyricsSearchTrack(
            name.Trim(),
            artist.Trim(),
            LyricsParsing.TryGetPath(root, out var album, "album") ? LyricsParsing.GetString(album, "title") : null,
            LyricsParsing.GetNumber(root, "duration") * 1000);
    }

    private async Task<LyricsSearchTrack?> ResolveMusicBrainzAsync(string isrc, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://musicbrainz.org/ws/2/isrc/{isrc}?inc=artist-credits&fmt=json",
            cancellationToken);
        if (document is null ||
            !LyricsParsing.TryGetPath(document.RootElement, out var recordings, "recordings") ||
            recordings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var recording in recordings.EnumerateArray())
        {
            var name = LyricsParsing.GetString(recording, "title");
            var artist = JoinArtistCredit(recording);
            if (!string.IsNullOrWhiteSpace(name) && artist.Length > 0)
            {
                return new LyricsSearchTrack(name.Trim(), artist, null, LyricsParsing.GetNumber(recording, "length"));
            }
        }

        return null;
    }

    private static string JoinArtistCredit(JsonElement recording)
    {
        if (!LyricsParsing.TryGetPath(recording, out var credits, "artist-credit") ||
            credits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var credit in credits.EnumerateArray())
        {
            builder.Append(LyricsParsing.GetString(credit, "name"));
            builder.Append(LyricsParsing.GetString(credit, "joinphrase"));
        }

        return builder.ToString().Trim();
    }

    private async Task<JsonDocument?> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    // ISRC 固定 12 位：2 位国家码 + 3 位登记者码 + 2 位年份 + 5 位编号。
    private static bool IsValidIsrc(string? isrc) =>
        isrc is { Length: 12 } &&
        isrc.Take(2).All(char.IsAsciiLetterUpper) &&
        isrc.Skip(2).Take(3).All(char.IsAsciiLetterOrDigit) &&
        isrc.Skip(5).All(char.IsAsciiDigit);

    private async Task<LyricsSearchTrack?> ResolveStorefrontAsync(
        NowPlayingInfo track,
        CancellationToken cancellationToken)
    {
        var trackId = track.PlayParameters?.Id;
        if (!long.TryParse(trackId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        using var document = await GetJsonAsync(
            $"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(trackId!)}&country=jp&entity=song",
            cancellationToken);
        if (document is null ||
            !LyricsParsing.TryGetPath(document.RootElement, out var results, "results") ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (!LyricsParsing.TryGetInt64(result, out var resolvedId, "trackId") ||
                !string.Equals(resolvedId.ToString(CultureInfo.InvariantCulture), trackId, StringComparison.Ordinal))
            {
                continue;
            }

            var name = LyricsParsing.GetString(result, "trackName");
            var artist = LyricsParsing.GetString(result, "artistName");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(artist))
            {
                continue;
            }

            return new LyricsSearchTrack(
                name,
                artist,
                LyricsParsing.GetString(result, "collectionName"),
                LyricsParsing.GetNumber(result, "trackTimeMillis"));
        }

        return null;
    }
}

internal abstract class HttpLyricsProvider(HttpClient httpClient) : ILyricsProvider
{
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    protected HttpClient HttpClient { get; } = httpClient;

    protected virtual TimeSpan RateLimitCooldown => TimeSpan.FromMinutes(1);

    /// <summary>
    /// 有些源限流时仍回 HTTP 200，只在 body 里标出错误码；由子类识别。
    /// </summary>
    protected virtual bool IsRateLimited(JsonElement root) => false;

    public abstract LyricsSource Source { get; }

    public abstract Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null);

    protected static void ConsiderCandidate(
        LyricsSnapshot snapshot,
        bool includeTranslation,
        ref LyricsSnapshot best,
        Action<LyricsSnapshot>? onCandidate)
    {
        if (snapshot.Lines.Count > 0 &&
            (best.Lines.Count == 0 || LyricsSelection.Compare(snapshot, best, includeTranslation) > 0))
        {
            best = snapshot;
            onCandidate?.Invoke(snapshot);
        }
    }

    protected async Task<JsonDocument?> SendJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _retryAfter)
        {
            request.Dispose();
            return null;
        }

        try
        {
            using (request)
            using (var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken))
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _retryAfter = response.Headers.RetryAfter?.Date ??
                        DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (IsRateLimited(document.RootElement))
                {
                    _retryAfter = DateTimeOffset.UtcNow.Add(RateLimitCooldown);
                    document.Dispose();
                    return null;
                }

                return document;
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static LyricsSnapshot Snapshot(
        LyricsSource source,
        string? original,
        string? translation = null)
    {
        var lines = LyricsParsing.ParseLrc(original);
        if (lines.Count > 0)
        {
            return new LyricsSnapshot(
                LyricsParsing.MergeTimestampTranslations(lines, LyricsParsing.ParseLrc(translation)),
                true,
                source);
        }

        var plainLines = LyricsParsing.ParsePlain(original);
        if (plainLines.Count == 0)
        {
            return new LyricsSnapshot([], false, source);
        }

        var translations = LyricsParsing.ParsePlain(translation);
        var merged = plainLines.Select((line, index) =>
            line with
            {
                Translation = index < translations.Count &&
                    LyricsParsing.IsChineseTranslation(translations[index].Text)
                        ? translations[index].Text
                        : null
            }).ToArray();
        return new LyricsSnapshot(merged, false, source);
    }
}

internal sealed class LrclibLyricsProvider(HttpClient httpClient) : HttpLyricsProvider(httpClient)
{
    private static readonly Uri BaseUri = new("https://lrclib.net/api/get");

    public override LyricsSource Source => LyricsSource.Lrclib;

    public override async Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null)
    {
        var best = new LyricsSnapshot([], false, Source);
        foreach (var track in tracks.Where(LyricsMatching.HasSearchMetadata))
        {
            using var document = await SendJsonAsync(
                new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(track)),
                cancellationToken);
            if (document is null)
            {
                continue;
            }

            var root = document.RootElement;
            var synced = LyricsParsing.GetString(root, "syncedLyrics");
            var snapshot = Snapshot(Source, string.IsNullOrWhiteSpace(synced)
                ? LyricsParsing.GetString(root, "plainLyrics")
                : synced);
            ConsiderCandidate(snapshot, includeTranslation, ref best, onCandidate);
            if (LyricsSelection.IsSatisfactory(snapshot, includeTranslation))
            {
                return snapshot;
            }
        }

        return best;
    }

    private static Uri BuildRequestUri(LyricsSearchTrack track)
    {
        var parameters = new List<string>
        {
            $"track_name={Uri.EscapeDataString(track.Name!)}",
            $"artist_name={Uri.EscapeDataString(track.ArtistName!)}"
        };
        if (!string.IsNullOrWhiteSpace(track.AlbumName))
        {
            parameters.Add($"album_name={Uri.EscapeDataString(track.AlbumName)}");
        }

        var duration = LyricsMatching.DurationSeconds(track);
        if (duration is >= 1 and <= 3600)
        {
            parameters.Add($"duration={duration.ToString(CultureInfo.InvariantCulture)}");
        }

        return new UriBuilder(BaseUri) { Query = string.Join('&', parameters) }.Uri;
    }
}

internal sealed class NeteaseLyricsProvider(HttpClient httpClient)
    : HttpLyricsProvider(httpClient), ILyricsContentSearchProvider
{
    private const string SongSearchType = "1";
    private const string LyricSearchType = "1006";
    private const int MaximumLyricQueries = 2;
    private const int MaximumLyricCandidates = 3;
    private static readonly Uri SearchUri = new("https://music.163.com/api/search/get/web");
    private static readonly Uri Referrer = new("https://music.163.com/");

    public override LyricsSource Source => LyricsSource.Netease;

    // 实测恢复约需两分钟。
    protected override TimeSpan RateLimitCooldown => TimeSpan.FromMinutes(2);

    // 网易云限流时回 HTTP 200 + {"code":406,"msg":"操作频繁，请稍候再试"}。
    protected override bool IsRateLimited(JsonElement root) =>
        LyricsParsing.TryGetInt64(root, out var code, "code") && code == 406;

    public override async Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null)
    {
        var searchableTracks = tracks.Where(LyricsMatching.HasSearchMetadata).ToArray();
        if (searchableTracks.Length == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var best = new LyricsSnapshot([], false, Source);
        var visited = new HashSet<long>();
        foreach (var track in searchableTracks)
        {
            using var search = await SendJsonAsync(
                CreateSearchRequest($"{LyricsMatching.SearchTitle(track.Name)} {LyricsMatching.PrimaryArtist(track.ArtistName)}", SongSearchType),
                cancellationToken);
            if (search is null)
            {
                continue;
            }

            foreach (var id in FindSongs(search.RootElement, track))
            {
                if (!visited.Add(id))
                {
                    continue;
                }

                var snapshot = await FetchLyricsAsync(id, includeTranslation, cancellationToken);
                if (snapshot is null)
                {
                    continue;
                }

                ConsiderCandidate(snapshot, includeTranslation, ref best, onCandidate);
                if (LyricsSelection.IsSatisfactory(snapshot, includeTranslation))
                {
                    return snapshot;
                }
            }
        }

        return best;
    }

    public async Task<LyricsSnapshot> SearchByLyricsAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        IReadOnlyList<LyricLineInfo> referenceLyrics,
        bool includeTranslation,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null)
    {
        var best = new LyricsSnapshot([], false, Source);
        var visited = new HashSet<long>();
        var fetched = 0;
        foreach (var query in LyricsMatching.SelectLyricQueries(referenceLyrics, MaximumLyricQueries))
        {
            using var search = await SendJsonAsync(CreateSearchRequest(query, LyricSearchType), cancellationToken);
            if (search is null)
            {
                continue;
            }

            foreach (var id in FindSongsByDuration(search.RootElement, tracks))
            {
                if (!visited.Add(id))
                {
                    continue;
                }

                if (fetched++ >= MaximumLyricCandidates)
                {
                    return best;
                }

                // 歌词搜索不看标题，只有内容与 Cider 歌词足够重合才算同一首歌。
                var snapshot = await FetchLyricsAsync(id, includeTranslation, cancellationToken);
                if (snapshot is null ||
                    LyricsMatching.ContentCoverage(referenceLyrics, snapshot.Lines) < LyricsMatching.MinimumContentCoverage)
                {
                    continue;
                }

                ConsiderCandidate(snapshot, includeTranslation, ref best, onCandidate);
                if (LyricsSelection.IsSatisfactory(snapshot, includeTranslation))
                {
                    return snapshot;
                }
            }
        }

        return best;
    }

    private static HttpRequestMessage CreateSearchRequest(string query, string type)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SearchUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = query,
                ["type"] = type,
                ["offset"] = "0",
                ["limit"] = "10"
            })
        };
        request.Headers.Referrer = Referrer;
        return request;
    }

    private async Task<LyricsSnapshot?> FetchLyricsAsync(
        long id,
        bool includeTranslation,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1");
        request.Headers.Referrer = Referrer;
        using var lyrics = await SendJsonAsync(request, cancellationToken);
        if (lyrics is null)
        {
            return null;
        }

        var root = lyrics.RootElement;
        var original = LyricsParsing.GetNestedString(root, "lrc", "lyric");
        var translation = includeTranslation
            ? LyricsParsing.GetNestedString(root, "tlyric", "lyric")
            : null;
        return Snapshot(Source, original, translation);
    }

    private static IReadOnlyList<long> FindSongsByDuration(JsonElement root, IReadOnlyList<LyricsSearchTrack> tracks)
    {
        if (!LyricsParsing.TryGetPath(root, out var songs, "result", "songs") ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var expectedDurations = tracks
            .Select(LyricsMatching.DurationSeconds)
            .Where(duration => duration > 0)
            .ToArray();
        var candidates = new List<(long Id, double Distance)>();
        foreach (var song in songs.EnumerateArray().Take(10))
        {
            if (!LyricsParsing.TryGetInt64(song, out var id, "id") || id == 0)
            {
                continue;
            }

            // 时长未知时无法筛选，交给内容比对把关。
            var duration = LyricsParsing.GetNumber(song, "duration", "dt") / 1000;
            var distance = expectedDurations.Length == 0 || duration <= 0
                ? 0
                : expectedDurations.Min(expected => Math.Abs(expected - duration));
            if (distance <= 3)
            {
                candidates.Add((id, distance));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Id)
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<long> FindSongs(JsonElement root, LyricsSearchTrack track)
    {
        if (!LyricsParsing.TryGetPath(root, out var songs, "result", "songs") ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var confident = new List<long>();
        var related = new List<long>();
        foreach (var song in songs.EnumerateArray().Take(10))
        {
            var name = LyricsParsing.GetString(song, "name");
            var artist = LyricsParsing.JoinNames(song, "artists", "ar");
            var duration = LyricsParsing.GetNumber(song, "duration", "dt") / 1000;
            if (LyricsMatching.IsConfidentMatch(track, name, artist, duration) &&
                LyricsParsing.TryGetInt64(song, out var id, "id") && id != 0)
            {
                confident.Add(id);
                continue;
            }

            if (LyricsMatching.IsLikelySameRecording(track, artist, duration) &&
                LyricsParsing.TryGetInt64(song, out var relatedId, "id") && relatedId != 0)
            {
                related.Add(relatedId);
            }
        }

        if (confident.Count > 0)
        {
            return confident.Distinct().ToArray();
        }

        var relatedIds = related.Distinct().ToArray();
        return relatedIds.Length == 1 ? relatedIds : [];
    }
}

internal sealed class QqMusicLyricsProvider(HttpClient httpClient) : HttpLyricsProvider(httpClient)
{
    public override LyricsSource Source => LyricsSource.QqMusic;

    public override async Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null)
    {
        var searchableTracks = tracks.Where(LyricsMatching.HasSearchMetadata).ToArray();
        if (searchableTracks.Length == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var best = new LyricsSnapshot([], false, Source);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in searchableTracks)
        {
            var query = Uri.EscapeDataString($"{LyricsMatching.SearchTitle(track.Name)} {LyricsMatching.PrimaryArtist(track.ArtistName)}");
            var searchRequest = CreateRequest(
                $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=10&w={query}");
            using var search = await SendJsonAsync(searchRequest, cancellationToken);
            if (search is null)
            {
                continue;
            }

            foreach (var songMid in FindSongs(search.RootElement, track))
            {
                if (!visited.Add(songMid))
                {
                    continue;
                }

                var lyricRequest = CreateRequest(
                    $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=1");
                using var lyrics = await SendJsonAsync(lyricRequest, cancellationToken);
                if (lyrics is null)
                {
                    continue;
                }

                var original = LyricsParsing.DecodePossibleBase64(LyricsParsing.GetString(lyrics.RootElement, "lyric"));
                var translation = includeTranslation
                    ? LyricsParsing.DecodePossibleBase64(LyricsParsing.GetString(lyrics.RootElement, "trans"))
                    : null;
                var snapshot = Snapshot(Source, original, translation);
                ConsiderCandidate(snapshot, includeTranslation, ref best, onCandidate);
                if (LyricsSelection.IsSatisfactory(snapshot, includeTranslation))
                {
                    return snapshot;
                }
            }
        }

        return best;
    }

    private static HttpRequestMessage CreateRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        return request;
    }

    private static IReadOnlyList<string> FindSongs(JsonElement root, LyricsSearchTrack track)
    {
        if (!LyricsParsing.TryGetPath(root, out var songs, "data", "song", "list") ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var confident = new List<string>();
        var related = new List<string>();
        foreach (var song in songs.EnumerateArray().Take(10))
        {
            var name = LyricsParsing.GetString(song, "songname", "title", "name");
            var artist = LyricsParsing.JoinNames(song, "singer");
            var duration = LyricsParsing.GetNumber(song, "interval");
            var mid = LyricsParsing.GetString(song, "songmid", "mid");
            if (!string.IsNullOrWhiteSpace(mid) && LyricsMatching.IsConfidentMatch(track, name, artist, duration))
            {
                confident.Add(mid);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mid) &&
                LyricsMatching.IsLikelySameRecording(track, artist, duration))
            {
                related.Add(mid);
            }
        }

        if (confident.Count > 0)
        {
            return confident.Distinct(StringComparer.Ordinal).ToArray();
        }

        var relatedIds = related.Distinct(StringComparer.Ordinal).ToArray();
        return relatedIds.Length == 1 ? relatedIds : [];
    }
}

internal sealed class MusixmatchLyricsProvider(HttpClient httpClient) : HttpLyricsProvider(httpClient)
{
    private const string BaseUri = "https://api.musixmatch.com/ws/1.1/";

    public override LyricsSource Source => LyricsSource.Musixmatch;

    public override async Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate = null)
    {
        var searchableTracks = tracks.Where(LyricsMatching.HasSearchMetadata).ToArray();
        if (searchableTracks.Length == 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return new LyricsSnapshot([], false, Source);
        }

        var best = new LyricsSnapshot([], false, Source);
        var visited = new HashSet<long>();
        foreach (var track in searchableTracks)
        {
            using var match = await SendJsonAsync(
                CreateRequest("matcher.track.get", apiKey,
                    ("q_track", track.Name),
                    ("q_artist", track.ArtistName),
                    ("f_has_lyrics", "1")),
                cancellationToken);
            if (match is null ||
                !LyricsParsing.TryGetPath(match.RootElement, out var matchedTrack, "message", "body", "track") ||
                !LyricsParsing.TryGetInt64(matchedTrack, out var trackId, "track_id") ||
                trackId == 0 ||
                !LyricsMatching.IsConfidentMatch(
                    track,
                    LyricsParsing.GetString(matchedTrack, "track_name"),
                    LyricsParsing.GetString(matchedTrack, "artist_name"),
                    LyricsParsing.GetNumber(matchedTrack, "track_length")) ||
                !visited.Add(trackId))
            {
                continue;
            }

            var snapshot = await FetchTrackLyricsAsync(trackId, includeTranslation, apiKey, cancellationToken, onCandidate);
            ConsiderCandidate(snapshot, includeTranslation, ref best, onCandidate);
            if (LyricsSelection.IsSatisfactory(snapshot, includeTranslation))
            {
                return snapshot;
            }
        }

        return best;
    }

    private async Task<LyricsSnapshot> FetchTrackLyricsAsync(
        long trackId,
        bool includeTranslation,
        string apiKey,
        CancellationToken cancellationToken,
        Action<LyricsSnapshot>? onCandidate)
    {
        using var subtitle = await SendJsonAsync(
            CreateRequest("track.subtitle.get", apiKey,
                ("track_id", trackId.ToString(CultureInfo.InvariantCulture)),
                ("subtitle_format", "lrc")),
            cancellationToken);
        var original = subtitle is null
            ? null
            : LyricsParsing.GetNestedString(subtitle.RootElement, "message", "body", "subtitle", "subtitle_body");

        if (string.IsNullOrWhiteSpace(original))
        {
            using var plainLyrics = await SendJsonAsync(
                CreateRequest("track.lyrics.get", apiKey,
                    ("track_id", trackId.ToString(CultureInfo.InvariantCulture))),
                cancellationToken);
            original = plainLyrics is null
                ? null
                : LyricsParsing.GetNestedString(plainLyrics.RootElement, "message", "body", "lyrics", "lyrics_body");
        }

        var snapshot = Snapshot(Source, original);
        if (snapshot.Lines.Count > 0)
        {
            onCandidate?.Invoke(snapshot);
        }
        if (!includeTranslation || snapshot.Lines.Count == 0)
        {
            return snapshot;
        }

        using var translations = await SendJsonAsync(
            CreateRequest("track.lyrics.translation.get", apiKey,
                ("track_id", trackId.ToString(CultureInfo.InvariantCulture)),
                ("selected_language", "zh")),
            cancellationToken);
        return translations is null
            ? snapshot
            : snapshot with { Lines = MergeTranslations(snapshot.Lines, translations.RootElement) };
    }

    private static HttpRequestMessage CreateRequest(
        string endpoint,
        string apiKey,
        params (string Name, string Value)[] parameters)
    {
        var query = parameters
            .Append((Name: "format", Value: "json"))
            .Append((Name: "apikey", Value: apiKey))
            .Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value)}");
        return new HttpRequestMessage(HttpMethod.Get, $"{BaseUri}{endpoint}?{string.Join('&', query)}");
    }

    private static IReadOnlyList<LyricLineInfo> MergeTranslations(
        IReadOnlyList<LyricLineInfo> lines,
        JsonElement root)
    {
        if (!LyricsParsing.TryGetPath(root, out var list, "message", "body", "translations_list") ||
            list.ValueKind != JsonValueKind.Array)
        {
            return lines;
        }

        var translations = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (!LyricsParsing.TryGetPath(item, out var translation, "translation"))
            {
                continue;
            }

            var matchedLine = LyricsMatching.Normalize(LyricsParsing.GetString(translation, "matched_line"));
            var description = LyricsParsing.GetString(translation, "description", "translation");
            if (matchedLine.Length == 0 ||
                description is null ||
                !LyricsParsing.IsChineseTranslation(description))
            {
                continue;
            }

            if (!translations.TryGetValue(matchedLine, out var values))
            {
                values = new Queue<string>();
                translations[matchedLine] = values;
            }

            values.Enqueue(description);
        }

        return lines.Select(line =>
        {
            var key = LyricsMatching.Normalize(line.Text);
            return translations.TryGetValue(key, out var values) && values.Count > 0
                ? line with { Translation = values.Dequeue() }
                : line;
        }).ToArray();
    }
}

internal static class LyricsMatching
{
    public static bool HasSearchMetadata(LyricsSearchTrack track) =>
        track.Name.Length > 0 && track.ArtistName.Length > 0;

    public static int DurationSeconds(NowPlayingInfo track) =>
        (int)Math.Round(track.DurationInMillis / 1000, MidpointRounding.AwayFromZero);

    public static int DurationSeconds(LyricsSearchTrack track) =>
        (int)Math.Round(track.DurationInMillis / 1000, MidpointRounding.AwayFromZero);

    public static bool IsConfidentMatch(
        NowPlayingInfo expected,
        string? candidateTitle,
        string? candidateArtist,
        double candidateDurationSeconds) =>
        IsConfidentMatch(LyricsSearchTrack.From(expected), candidateTitle, candidateArtist, candidateDurationSeconds);

    public static bool IsConfidentMatch(
        LyricsSearchTrack expected,
        string? candidateTitle,
        string? candidateArtist,
        double candidateDurationSeconds)
    {
        var expectedDuration = DurationSeconds(expected);
        var durationKnown = expectedDuration > 0 && candidateDurationSeconds > 0;
        if ((durationKnown && Math.Abs(expectedDuration - candidateDurationSeconds) > 3) ||
            !IsArtistMatch(expected.ArtistName, candidateArtist))
        {
            return false;
        }

        var expectedTitle = MatchKey(expected.Name);
        if (expectedTitle.Length == 0)
        {
            return false;
        }

        if (string.Equals(expectedTitle, MatchKey(candidateTitle), StringComparison.Ordinal))
        {
            return true;
        }

        // 去掉 feat.、Remaster 等不改变歌词的标记后再比；同名不同版本缺少时长佐证时不放行。
        var expectedBase = MatchKey(StripNeutralTags(expected.Name));
        return durationKnown &&
            expectedBase.Length > 0 &&
            string.Equals(expectedBase, MatchKey(StripNeutralTags(candidateTitle)), StringComparison.Ordinal);
    }

    public static bool IsLikelySameRecording(
        LyricsSearchTrack expected,
        string? candidateArtist,
        double candidateDurationSeconds)
    {
        var expectedArtists = ArtistKeys(expected.ArtistName);
        var actualArtists = ArtistKeys(candidateArtist);
        var expectedDuration = DurationSeconds(expected);
        return expectedArtists.Count > 0 &&
            expectedArtists.SetEquals(actualArtists) &&
            expectedDuration > 0 &&
            candidateDurationSeconds > 0 &&
            Math.Abs(expectedDuration - candidateDurationSeconds) <= 3;
    }

    /// <summary>
    /// 艺人整体互相包含，或拆分后任一位相同即视为匹配（"A & B" 对 "B / A"）。
    /// </summary>
    public static bool IsArtistMatch(string? expected, string? actual)
    {
        var expectedKey = MatchKey(expected);
        var actualKey = MatchKey(actual);
        if (expectedKey.Length == 0 || actualKey.Length == 0)
        {
            return false;
        }

        return expectedKey.Contains(actualKey, StringComparison.Ordinal) ||
            actualKey.Contains(expectedKey, StringComparison.Ordinal) ||
            ArtistKeys(expected).Overlaps(ArtistKeys(actual));
    }

    /// <summary>
    /// 用于搜索的标题：去掉不改变歌词的标记，减少搜索引擎的噪声。
    /// </summary>
    public static string SearchTitle(string title)
    {
        var stripped = StripNeutralTags(title);
        return stripped.Length > 0 ? stripped : title;
    }

    /// <summary>
    /// 用于搜索的艺人：只取第一位，合作者留给匹配阶段核对。
    /// </summary>
    public static string PrimaryArtist(string artist) =>
        ArtistSeparator.Split(artist).Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? artist;

    // 只剥离不改变歌词的标记；Live、Remix、Acoustic、TV Size 等不同录音保留，交给精确比对区分。
    private static readonly Regex NeutralTag = new(
        @"\b(feat\.?|ft\.|featuring|remaster(ed)?|single version|album version|radio edit|from|explicit|clean|mono|stereo|bonus track)(?=\W|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BracketSegment = new(
        @"\s*[(\[（【]([^)\]）】]*)[)\]）】]", RegexOptions.CultureInvariant);
    private static readonly Regex DashSuffix = new(
        @"\s+[-–—]\s+(.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex BareFeaturing = new(
        @"\s+(feat\.?|ft\.|featuring)\s.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ArtistSeparator = new(
        @"\s*(?:&|,|，|、|/|;|；|×|\s+x\s+|\b(?:feat\.?|ft\.|featuring|vs\.?)(?=\s|$))\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string StripNeutralTags(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var stripped = BracketSegment.Replace(title, match =>
            NeutralTag.IsMatch(match.Groups[1].Value) ? string.Empty : match.Value);
        stripped = DashSuffix.Replace(stripped, match =>
            NeutralTag.IsMatch(match.Groups[1].Value) ? string.Empty : match.Value);
        return BareFeaturing.Replace(stripped, string.Empty).Trim();
    }

    private static HashSet<string> ArtistKeys(string? artist) =>
        string.IsNullOrWhiteSpace(artist)
            ? []
            : ArtistSeparator.Split(artist)
                .Select(MatchKey)
                .Where(key => key.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

    // 短句（如 "yeah"、"oh oh"）在不相关的歌词里也常出现，不参与内容比对。
    private const int MinimumContentLineLength = 4;
    public const double MinimumContentCoverage = 0.6;
    private const int MinimumQueryLineLength = 6;
    private const int MaximumQueryLineLength = 40;

    /// <summary>
    /// 从参考歌词中挑选适合做歌词搜索的句子：只出现一次、足够长且不过长的行，越长越优先。
    /// </summary>
    public static IReadOnlyList<string> SelectLyricQueries(IReadOnlyList<LyricLineInfo> reference, int count) =>
        reference
            .Select(line => (Text: line.Text.Trim(), Key: MatchKey(line.Text)))
            .GroupBy(line => line.Key)
            .Where(group => group.Key.Length is >= MinimumQueryLineLength and <= MaximumQueryLineLength &&
                group.Count() == 1)
            .Select(group => group.Single())
            .OrderByDescending(line => line.Key.Length)
            .Take(count)
            .Select(line => line.Text)
            .ToArray();

    /// <summary>
    /// 参考歌词中有多少比例的行出现在候选歌词里。候选按顺序拼接后再查找，
    /// 因此两边断行方式不同（一行拆成两行或两行合成一行）也能命中。
    /// </summary>
    public static double ContentCoverage(IReadOnlyList<LyricLineInfo> reference, IReadOnlyList<LyricLineInfo> candidate) =>
        ContentCoverage(ContentKeys(reference), candidate);

    /// <summary>
    /// 候选与参考歌词明显不是同一首：参考至少有 3 行可比对，且命中不足 30%。
    /// 门槛比反查宽松，只拦截错歌，容忍两边写法、断行和部分缺行的差异。
    /// </summary>
    public static bool ContradictsReference(IReadOnlyList<LyricLineInfo> reference, IReadOnlyList<LyricLineInfo> candidate)
    {
        var keys = ContentKeys(reference);
        return keys.Length >= MinimumContentEvidence &&
            ContentCoverage(keys, candidate) < MinimumReferenceAgreement;
    }

    private const int MinimumContentEvidence = 3;
    private const double MinimumReferenceAgreement = 0.3;

    private static string[] ContentKeys(IReadOnlyList<LyricLineInfo> reference) =>
        reference
            .Select(line => MatchKey(line.Text))
            .Where(key => key.Length >= MinimumContentLineLength)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static double ContentCoverage(string[] keys, IReadOnlyList<LyricLineInfo> candidate)
    {
        if (keys.Length == 0 || candidate.Count == 0)
        {
            return 0;
        }

        var text = MatchKey(string.Join('\n', candidate.Select(line => line.Text)));
        return (double)keys.Count(key => text.Contains(key, StringComparison.Ordinal)) / keys.Length;
    }

    /// <summary>
    /// 比对用的键：繁转简后再做 <see cref="Normalize"/>，使"周杰倫"与"周杰伦"相等。
    /// </summary>
    public static string MatchKey(string? value) =>
        Normalize(string.IsNullOrEmpty(value) ? value : ChineseTextConverter.ToSimplified(value));

    public static bool IsSameIdentity(LyricsSearchTrack first, LyricsSearchTrack second) =>
        string.Equals(MatchKey(first.Name), MatchKey(second.Name), StringComparison.Ordinal) &&
        string.Equals(MatchKey(first.ArtistName), MatchKey(second.ArtistName), StringComparison.Ordinal);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

internal static class LyricsParsing
{
    public static IReadOnlyList<LyricLineInfo> ParseLrc(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return [];
        }

        var parsed = new List<(double StartTime, string Text)>();
        foreach (var line in lyrics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var timestamps = new List<double>();
            var offset = 0;
            while (offset < line.Length && line[offset] == '[')
            {
                var closingBracket = line.IndexOf(']', offset + 1);
                if (closingBracket < 0 ||
                    !TryParseTimestamp(line.AsSpan(offset + 1, closingBracket - offset - 1), out var timestamp))
                {
                    break;
                }

                timestamps.Add(timestamp);
                offset = closingBracket + 1;
            }

            var text = line[offset..].Trim();
            if (timestamps.Count == 0 || text.Length == 0)
            {
                continue;
            }

            parsed.AddRange(timestamps.Select(timestamp => (timestamp, text)));
        }

        var ordered = parsed.OrderBy(line => line.StartTime).ToArray();
        return ordered.Select((line, index) => new LyricLineInfo(
            line.StartTime,
            index + 1 < ordered.Length ? ordered[index + 1].StartTime : null,
            line.Text)).ToArray();
    }

    public static IReadOnlyList<LyricLineInfo> ParsePlain(string? lyrics) =>
        string.IsNullOrWhiteSpace(lyrics)
            ? []
            : lyrics
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0 && !line.StartsWith("*******", StringComparison.Ordinal))
                .Select((line, index) => new LyricLineInfo(index, null, line))
                .ToArray();

    public static IReadOnlyList<LyricLineInfo> MergeTimestampTranslations(
        IReadOnlyList<LyricLineInfo> original,
        IReadOnlyList<LyricLineInfo> translations)
    {
        if (translations.Count == 0)
        {
            return original;
        }

        var unused = translations.Select(line => line).ToList();
        return original.Select(line =>
        {
            var exact = unused.FindIndex(candidate =>
                Math.Round(candidate.StartTime, 2) == Math.Round(line.StartTime, 2));
            var matchIndex = exact >= 0
                ? exact
                : FindNearestWithin(unused, line.StartTime, 0.5);
            if (matchIndex < 0)
            {
                return line;
            }

            var translation = unused[matchIndex].Text;
            unused.RemoveAt(matchIndex);
            return line with
            {
                Translation = IsChineseTranslation(translation) ? translation : null
            };
        }).ToArray();
    }

    public static string? DecodePossibleBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('[', StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return decoded.Contains('[') || decoded.Contains('\r') || decoded.Contains('\n')
                ? decoded
                : value;
        }
        catch (FormatException)
        {
            return value;
        }
    }

    public static bool IsChineseTranslation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hasHanCharacter = false;
        foreach (var character in value)
        {
            if (character is >= '\u3040' and <= '\u30ff' or >= '\u31f0' and <= '\u31ff')
            {
                return false;
            }

            hasHanCharacter |= character is >= '\u3400' and <= '\u4dbf' or
                >= '\u4e00' and <= '\u9fff' or
                >= '\uf900' and <= '\ufaff';
        }

        return hasHanCharacter;
    }

    public static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    public static string? GetNestedString(JsonElement element, params string[] path) =>
        TryGetPath(element, out var value, path) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryGetPath(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var part in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !TryGetProperty(value, part, out value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryGetInt64(JsonElement element, out long value, params string[] names)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.Number => property.Value.TryGetInt64(out value),
                JsonValueKind.String => long.TryParse(
                    property.Value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value),
                _ => false
            };
        }

        return false;
    }

    public static double GetNumber(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
            {
                return number;
            }
        }

        return 0;
    }

    public static string JoinNames(JsonElement element, params string[] arrayNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var name in arrayNames)
        {
            if (!TryGetProperty(element, name, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // 用 " / " 连接，匹配阶段才能把多位艺人拆开。
            return string.Join(" / ", array.EnumerateArray()
                .Select(item => GetString(item, "name"))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return string.Empty;
    }

    private static int FindNearestWithin(
        IReadOnlyList<LyricLineInfo> candidates,
        double startTime,
        double tolerance)
    {
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < candidates.Count; index++)
        {
            var distance = Math.Abs(candidates[index].StartTime - startTime);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestIndex = index;
                bestDistance = distance;
            }
        }

        return bestIndex;
    }

    private static bool TryParseTimestamp(ReadOnlySpan<char> value, out double timestamp)
    {
        timestamp = 0;
        var colon = value.IndexOf(':');
        if (colon <= 0 ||
            !int.TryParse(value[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(value[(colon + 1)..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or >= 60)
        {
            return false;
        }

        timestamp = minutes * 60 + seconds;
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
