using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Lyrider.Models;

namespace Lyrider.Services;

internal interface ILyricsProvider
{
    LyricsSource Source { get; }

    Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken);
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

internal sealed class AppleMusicTrackResolver(HttpClient httpClient) : ILyricsTrackResolver
{
    public async Task<IReadOnlyList<LyricsSearchTrack>> ResolveAsync(
        NowPlayingInfo track,
        CancellationToken cancellationToken)
    {
        var original = LyricsSearchTrack.From(track);
        var trackId = track.PlayParameters?.Id;
        if (!long.TryParse(trackId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return [original];
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(trackId)}&country=jp&entity=song");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [original];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!LyricsParsing.TryGetPath(document.RootElement, out var results, "results") ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [original];
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

            var resolved = new LyricsSearchTrack(
                name,
                artist,
                LyricsParsing.GetString(result, "collectionName"),
                LyricsParsing.GetNumber(result, "trackTimeMillis"));
            return LyricsMatching.IsSameIdentity(original, resolved)
                ? [original]
                : [original, resolved];
        }

        return [original];
    }
}

internal abstract class HttpLyricsProvider(HttpClient httpClient) : ILyricsProvider
{
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    protected HttpClient HttpClient { get; } = httpClient;

    public abstract LyricsSource Source { get; }

    public abstract Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken);

    protected async Task<JsonDocument?> SendJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _retryAfter)
        {
            request.Dispose();
            return null;
        }

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
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
            line with { Translation = index < translations.Count ? translations[index].Text : null }).ToArray();
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
        CancellationToken cancellationToken)
    {
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
            if (snapshot.Lines.Count > 0)
            {
                return snapshot;
            }
        }

        return new LyricsSnapshot([], false, Source);
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

internal sealed class NeteaseLyricsProvider(HttpClient httpClient) : HttpLyricsProvider(httpClient)
{
    private static readonly Uri SearchUri = new("https://music.163.com/api/search/get/web");

    public override LyricsSource Source => LyricsSource.Netease;

    public override async Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var searchableTracks = tracks.Where(LyricsMatching.HasSearchMetadata).ToArray();
        if (searchableTracks.Length == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

        long id = 0;
        foreach (var track in searchableTracks)
        {
            var searchRequest = new HttpRequestMessage(HttpMethod.Post, SearchUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["s"] = $"{track.Name} {track.ArtistName}",
                    ["type"] = "1",
                    ["offset"] = "0",
                    ["limit"] = "10"
                })
            };
            searchRequest.Headers.Referrer = new Uri("https://music.163.com/");

            using var search = await SendJsonAsync(searchRequest, cancellationToken);
            if (search is not null && TryFindSong(search.RootElement, track, out id))
            {
                break;
            }
        }

        if (id == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var lyricRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1");
        lyricRequest.Headers.Referrer = new Uri("https://music.163.com/");
        using var lyrics = await SendJsonAsync(lyricRequest, cancellationToken);
        if (lyrics is null)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var root = lyrics.RootElement;
        var original = LyricsParsing.GetNestedString(root, "lrc", "lyric");
        var translation = includeTranslation
            ? LyricsParsing.GetNestedString(root, "tlyric", "lyric")
            : null;
        return Snapshot(Source, original, translation);
    }

    private static bool TryFindSong(JsonElement root, LyricsSearchTrack track, out long id)
    {
        id = 0;
        if (!LyricsParsing.TryGetPath(root, out var songs, "result", "songs") ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var related = new List<long>();
        foreach (var song in songs.EnumerateArray())
        {
            var name = LyricsParsing.GetString(song, "name");
            var artist = LyricsParsing.JoinNames(song, "artists", "ar");
            var duration = LyricsParsing.GetNumber(song, "duration", "dt") / 1000;
            if (LyricsMatching.IsConfidentMatch(track, name, artist, duration) &&
                LyricsParsing.TryGetInt64(song, out id, "id"))
            {
                return true;
            }

            if (LyricsMatching.IsLikelySameRecording(track, artist, duration) &&
                LyricsParsing.TryGetInt64(song, out var relatedId, "id"))
            {
                related.Add(relatedId);
            }
        }

        if (related.Count != 1)
        {
            return false;
        }

        id = related[0];
        return true;
    }
}

internal sealed class QqMusicLyricsProvider(HttpClient httpClient) : HttpLyricsProvider(httpClient)
{
    public override LyricsSource Source => LyricsSource.QqMusic;

    public override async Task<LyricsSnapshot> FetchAsync(
        IReadOnlyList<LyricsSearchTrack> tracks,
        bool includeTranslation,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var searchableTracks = tracks.Where(LyricsMatching.HasSearchMetadata).ToArray();
        if (searchableTracks.Length == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var songMid = string.Empty;
        foreach (var track in searchableTracks)
        {
            var query = Uri.EscapeDataString($"{track.Name} {track.ArtistName}");
            var searchRequest = CreateRequest(
                $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=10&w={query}");
            using var search = await SendJsonAsync(searchRequest, cancellationToken);
            if (search is not null && TryFindSong(search.RootElement, track, out songMid))
            {
                break;
            }
        }

        if (songMid.Length == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var lyricRequest = CreateRequest(
            $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songMid)}&format=json&nobase64=1");
        using var lyrics = await SendJsonAsync(lyricRequest, cancellationToken);
        if (lyrics is null)
        {
            return new LyricsSnapshot([], false, Source);
        }

        var original = LyricsParsing.DecodePossibleBase64(LyricsParsing.GetString(lyrics.RootElement, "lyric"));
        var translation = includeTranslation
            ? LyricsParsing.DecodePossibleBase64(LyricsParsing.GetString(lyrics.RootElement, "trans"))
            : null;
        return Snapshot(Source, original, translation);
    }

    private static HttpRequestMessage CreateRequest(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        return request;
    }

    private static bool TryFindSong(JsonElement root, LyricsSearchTrack track, out string songMid)
    {
        songMid = string.Empty;
        if (!LyricsParsing.TryGetPath(root, out var songs, "data", "song", "list") ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var related = new List<string>();
        foreach (var song in songs.EnumerateArray())
        {
            var name = LyricsParsing.GetString(song, "songname", "title", "name");
            var artist = LyricsParsing.JoinNames(song, "singer");
            var duration = LyricsParsing.GetNumber(song, "interval");
            var mid = LyricsParsing.GetString(song, "songmid", "mid");
            if (!string.IsNullOrWhiteSpace(mid) && LyricsMatching.IsConfidentMatch(track, name, artist, duration))
            {
                songMid = mid;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(mid) &&
                LyricsMatching.IsLikelySameRecording(track, artist, duration))
            {
                related.Add(mid);
            }
        }

        if (related.Count != 1)
        {
            return false;
        }

        songMid = related[0];
        return true;
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
        CancellationToken cancellationToken)
    {
        var searchableTracks = tracks.Where(LyricsMatching.HasSearchMetadata).ToArray();
        if (searchableTracks.Length == 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            return new LyricsSnapshot([], false, Source);
        }

        long trackId = 0;
        foreach (var track in searchableTracks)
        {
            using var match = await SendJsonAsync(
                CreateRequest("matcher.track.get", apiKey,
                    ("q_track", track.Name),
                    ("q_artist", track.ArtistName),
                    ("f_has_lyrics", "1")),
                cancellationToken);
            if (match is not null &&
                LyricsParsing.TryGetPath(match.RootElement, out var matchedTrack, "message", "body", "track") &&
                LyricsParsing.TryGetInt64(matchedTrack, out trackId, "track_id") &&
                LyricsMatching.IsConfidentMatch(
                    track,
                    LyricsParsing.GetString(matchedTrack, "track_name"),
                    LyricsParsing.GetString(matchedTrack, "artist_name"),
                    LyricsParsing.GetNumber(matchedTrack, "track_length")))
            {
                break;
            }

            trackId = 0;
        }

        if (trackId == 0)
        {
            return new LyricsSnapshot([], false, Source);
        }

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
            if (matchedLine.Length == 0 || string.IsNullOrWhiteSpace(description))
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
        var expectedTitle = Normalize(expected.Name);
        var expectedArtist = Normalize(expected.ArtistName);
        var actualTitle = Normalize(candidateTitle);
        var actualArtist = Normalize(candidateArtist);
        if (expectedTitle.Length == 0 ||
            expectedArtist.Length == 0 ||
            actualArtist.Length == 0 ||
            !string.Equals(expectedTitle, actualTitle, StringComparison.Ordinal) ||
            !(string.Equals(expectedArtist, actualArtist, StringComparison.Ordinal) ||
                expectedArtist.Contains(actualArtist, StringComparison.Ordinal) ||
                actualArtist.Contains(expectedArtist, StringComparison.Ordinal)))
        {
            return false;
        }

        var expectedDuration = DurationSeconds(expected);
        return expectedDuration <= 0 ||
            candidateDurationSeconds <= 0 ||
            Math.Abs(expectedDuration - candidateDurationSeconds) <= 3;
    }

    public static bool IsLikelySameRecording(
        LyricsSearchTrack expected,
        string? candidateArtist,
        double candidateDurationSeconds)
    {
        var expectedArtist = Normalize(expected.ArtistName);
        var actualArtist = Normalize(candidateArtist);
        var expectedDuration = DurationSeconds(expected);
        return expectedArtist.Length > 0 &&
            string.Equals(expectedArtist, actualArtist, StringComparison.Ordinal) &&
            expectedDuration > 0 &&
            candidateDurationSeconds > 0 &&
            Math.Abs(expectedDuration - candidateDurationSeconds) <= 3;
    }

    public static bool IsSameIdentity(LyricsSearchTrack first, LyricsSearchTrack second) =>
        string.Equals(Normalize(first.Name), Normalize(second.Name), StringComparison.Ordinal) &&
        string.Equals(Normalize(first.ArtistName), Normalize(second.ArtistName), StringComparison.Ordinal);

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
            return line with { Translation = translation };
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
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
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

            return string.Join(' ', array.EnumerateArray()
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
