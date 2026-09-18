using System.Globalization;
using System.Net;
using System.Text.Json;
using Lyrider.Models;

namespace Lyrider.Services;

public sealed class LyricsService : IDisposable
{
    private static readonly Uri LrclibBaseUri = new("https://lrclib.net/api/get");
    private readonly HttpClient _httpClient;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public LyricsService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Lyrider/1.0 (+https://github.com/Ayor1337/Lyrider)");
    }

    public async Task<LyricsSnapshot> ResolveAsync(
        NowPlayingInfo track,
        IReadOnlyList<LyricLineInfo> ciderLyrics,
        CancellationToken cancellationToken = default)
    {
        if (ciderLyrics.Count > 0)
        {
            return new LyricsSnapshot(ciderLyrics, track.HasTimeSyncedLyrics);
        }

        if (string.IsNullOrWhiteSpace(track.Name) ||
            string.IsNullOrWhiteSpace(track.ArtistName) ||
            DateTimeOffset.UtcNow < _retryAfter)
        {
            return LyricsSnapshot.Empty;
        }

        try
        {
            using var response = await _httpClient.GetAsync(BuildRequestUri(track), cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _retryAfter = response.Headers.RetryAfter?.Date ??
                    DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                return LyricsSnapshot.Empty;
            }

            if (!response.IsSuccessStatusCode)
            {
                return LyricsSnapshot.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var syncedLyrics = GetString(root, "syncedLyrics");
            var syncedLines = ParseSyncedLyrics(syncedLyrics);
            if (syncedLines.Count > 0)
            {
                return new LyricsSnapshot(syncedLines, true);
            }

            var plainLines = ParsePlainLyrics(GetString(root, "plainLyrics"));
            return plainLines.Count > 0
                ? new LyricsSnapshot(plainLines, false)
                : LyricsSnapshot.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LyricsSnapshot.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return LyricsSnapshot.Empty;
        }
    }

    private static Uri BuildRequestUri(NowPlayingInfo track)
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

        var duration = Math.Round(track.DurationInMillis / 1000, MidpointRounding.AwayFromZero);
        if (duration is >= 1 and <= 3600)
        {
            parameters.Add($"duration={duration.ToString(CultureInfo.InvariantCulture)}");
        }

        return new UriBuilder(LrclibBaseUri) { Query = string.Join('&', parameters) }.Uri;
    }

    private static IReadOnlyList<LyricLineInfo> ParseSyncedLyrics(string? lyrics)
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

    private static IReadOnlyList<LyricLineInfo> ParsePlainLyrics(string? lyrics) =>
        string.IsNullOrWhiteSpace(lyrics)
            ? []
            : lyrics
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0)
                .Select((line, index) => new LyricLineInfo(index, null, line))
                .ToArray();

    private static string? GetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
