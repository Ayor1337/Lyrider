using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lyrider.Models;

namespace Lyrider.Services;

public sealed class CiderService : IDisposable
{
    private static readonly Uri NowPlayingUri = new("api/v1/playback/now-playing", UriKind.Relative);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _httpClient;
    private readonly HttpClient _lyricsHttpClient;
    private readonly TimeSpan _defaultLyricsRequestTimeout;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Uri _baseAddress;

    public CiderService(string? baseAddress = null)
        : this(
            baseAddress,
            new HttpClientHandler(),
            RequestTimeout)
    {
    }

    internal CiderService(
        string? baseAddress,
        HttpMessageHandler handler,
        TimeSpan defaultLyricsRequestTimeout)
    {
        _baseAddress = ParseBaseAddress(baseAddress);
        _defaultLyricsRequestTimeout = defaultLyricsRequestTimeout;
        _httpClient = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = RequestTimeout
        };
        _lyricsHttpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public bool TryUpdateBaseAddress(string? baseAddress)
    {
        if (!TryParseBaseAddress(baseAddress, out var uri))
        {
            return false;
        }

        _baseAddress = uri;
        return true;
    }

    public async Task<CiderResult> GetNowPlayingAsync(
        string? appToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, NowPlayingUri, appToken, null, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return CiderResult.Unauthorized(AppText.Get("Token 缺失或无效", "The token is missing or invalid"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return CiderResult.Error(AppText.Format("Cider API 返回 {0}", "Cider API returned {0}", (int)response.StatusCode));
            }

            var payload = await response.Content.ReadFromJsonAsync<CiderNowPlayingResponse>(
                _jsonOptions,
                cancellationToken);

            if (payload is null)
            {
                return CiderResult.Error(AppText.Get("Cider API 返回了空响应", "Cider API returned an empty response"));
            }

            if (!string.IsNullOrWhiteSpace(payload.Status) &&
                !string.Equals(payload.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return CiderResult.Error(AppText.Get("Cider API 返回了错误状态", "Cider API returned an error status"));
            }

            return CiderResult.Connected(payload.Info);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CiderResult.Offline(AppText.Get("连接 Cider 超时", "The connection to Cider timed out"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return CiderResult.Offline(AppText.Get("无法连接 Cider，请确认应用已运行", "Could not connect to Cider. Make sure it is running"));
        }
        catch (JsonException)
        {
            return CiderResult.Error(AppText.Get("无法解析 Cider 返回的数据", "Could not parse the data returned by Cider"));
        }
        catch (Exception)
        {
            return CiderResult.Error(AppText.Get("读取 Cider 状态时发生错误", "An error occurred while reading Cider's status"));
        }
    }

    public async Task<PlaybackStatus?> GetPlaybackStatusAsync(
        string? appToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var playingTask = GetJsonAsync("api/v1/playback/is-playing", appToken, cancellationToken);
            var volumeTask = GetJsonAsync("api/v1/playback/volume", appToken, cancellationToken);
            await Task.WhenAll(playingTask, volumeTask);

            using var playing = await playingTask;
            using var volume = await volumeTask;
            var isPlaying = TryFindBoolean(playing?.RootElement, "is_playing", "isPlaying") ?? false;
            var volumeValue = TryFindDouble(volume?.RootElement, "volume") ?? 1;
            return new PlaybackStatus(isPlaying, Math.Clamp(volumeValue, 0, 1));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<QueueSnapshot> GetQueueAsync(
        string? appToken,
        string? currentTrackId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetJsonAsync("api/v1/playback/queue", appToken, cancellationToken);
            if (document is null || !TryFindArray(document.RootElement, out var array, "queue", "items", "data"))
            {
                return new QueueSnapshot([], -1);
            }

            var items = new List<QueueItemInfo>();
            var sourceIndex = 0;

            foreach (var element in array.EnumerateArray())
            {
                var name = TryFindString(element, "name", "title");
                if (string.IsNullOrWhiteSpace(name))
                {
                    sourceIndex++;
                    continue;
                }

                items.Add(new QueueItemInfo(
                    sourceIndex,
                    TryFindString(element, "id", "catalogId", "songId"),
                    name,
                    TryFindString(element, "artistName", "artist") ?? "—",
                    TryFindString(element, "albumName", "album") ?? "—",
                    TryFindDouble(element, "durationInMillis", "duration") ?? 0,
                    TryFindArtworkUrl(element),
                    TryFindNestedBoolean(element, "hasLyrics") ?? false,
                    TryFindNestedBoolean(element, "hasTimeSyncedLyrics") ?? false));
                sourceIndex++;
            }

            var currentIndex = TryFindCurrentIndex(document.RootElement) ?? -1;
            if (!string.IsNullOrWhiteSpace(currentTrackId))
            {
                var matchingIndex = items.FindIndex(item => string.Equals(
                    item.Id,
                    currentTrackId,
                    StringComparison.Ordinal));
                if (matchingIndex >= 0)
                {
                    currentIndex = matchingIndex;
                }
            }

            return new QueueSnapshot(items, currentIndex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueueSnapshot([], -1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new QueueSnapshot([], -1);
        }
    }

    public async Task<IReadOnlyList<LyricLineInfo>> GetLyricsAsync(
        string? trackId,
        string? appToken,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return [];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout ?? _defaultLyricsRequestTimeout);
            using var document = await GetLyricsDocumentAsync(trackId, appToken, timeout.Token);
            if (document is null || !TryFindArray(document.RootElement, out var array, "lyrics", "lines", "data"))
            {
                return [];
            }

            var lines = new List<LyricLineInfo>();
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var plainText = element.GetString();
                    if (!string.IsNullOrWhiteSpace(plainText))
                    {
                        lines.Add(new LyricLineInfo(lines.Count, null, plainText));
                    }

                    continue;
                }

                var text = TryFindString(element, "text", "line", "lyric", "words");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var startTime = TryFindTime(element, "startTime", "start", "begin", "time") ?? lines.Count;
                var endTime = TryFindTime(element, "endTime", "end", "finish");
                lines.Add(new LyricLineInfo(startTime, endTime, text));
            }

            return lines.OrderBy(line => line.StartTime).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<JsonDocument?> GetLyricsDocumentAsync(
        string trackId,
        string? appToken,
        CancellationToken cancellationToken)
    {
        // Cider 4 serves lyrics from the scoped /api/v2 surface, where the app token is
        // checked against the "lyrics" scope. The /api/v1 lyrics route belongs to the
        // client's own session and always rejects the app token with 401, so it only
        // stays as a fallback for older Cider versions that lack /api/v2.
        foreach (var path in new[] { "api/v2/lyrics/", "api/v1/lyrics/" })
        {
            var document = await GetJsonAsync(
                _lyricsHttpClient,
                $"{path}{Uri.EscapeDataString(trackId)}",
                appToken,
                cancellationToken);
            if (document is not null)
            {
                return document;
            }
        }

        return null;
    }

    public Task<bool> TogglePlayPauseAsync(string? appToken, CancellationToken cancellationToken = default) =>
        SendCommandAsync("api/v1/playback/playpause", appToken, null, cancellationToken);

    public Task<bool> PlayNextAsync(string? appToken, CancellationToken cancellationToken = default) =>
        SendCommandAsync("api/v1/playback/next", appToken, null, cancellationToken);

    public Task<bool> PlayPreviousAsync(string? appToken, CancellationToken cancellationToken = default) =>
        SendCommandAsync("api/v1/playback/previous", appToken, null, cancellationToken);

    public Task<bool> ToggleShuffleAsync(string? appToken, CancellationToken cancellationToken = default) =>
        SendCommandAsync("api/v1/playback/toggle-shuffle", appToken, null, cancellationToken);

    public Task<bool> ToggleRepeatAsync(string? appToken, CancellationToken cancellationToken = default) =>
        SendCommandAsync("api/v1/playback/toggle-repeat", appToken, null, cancellationToken);

    public Task<bool> SeekAsync(
        double position,
        string? appToken,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            "api/v1/playback/seek",
            appToken,
            new { position = Math.Max(0, position) },
            cancellationToken);

    public Task<bool> ChangeQueueIndexAsync(
        int index,
        string? appToken,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            "api/v1/playback/queue/change-to-index",
            appToken,
            new { index },
            cancellationToken);

    public void Dispose()
    {
        _httpClient.Dispose();
        _lyricsHttpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri relativeUri,
        string? appToken,
        object? body,
        CancellationToken cancellationToken) =>
        await SendAsync(_httpClient, method, relativeUri, appToken, body, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpMethod method,
        Uri relativeUri,
        string? appToken,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseAddress, relativeUri));
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            request.Headers.TryAddWithoutValidation("apptoken", appToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument?> GetJsonAsync(
        string relativeUri,
        string? appToken,
        CancellationToken cancellationToken) =>
        await GetJsonAsync(_httpClient, relativeUri, appToken, cancellationToken);

    private async Task<JsonDocument?> GetJsonAsync(
        HttpClient httpClient,
        string relativeUri,
        string? appToken,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            httpClient,
            HttpMethod.Get,
            new Uri(relativeUri, UriKind.Relative),
            appToken,
            null,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<bool> SendCommandAsync(
        string relativeUri,
        string? appToken,
        object? body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                new Uri(relativeUri, UriKind.Relative),
                appToken,
                body,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Uri ParseBaseAddress(string? baseAddress) =>
        TryParseBaseAddress(baseAddress, out var uri)
            ? uri
            : new Uri(AppSettings.DefaultApiBaseUrl);

    private static bool TryParseBaseAddress(string? baseAddress, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var normalized = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed.AbsoluteUri
            : $"{parsed.AbsoluteUri}/";
        uri = new Uri(normalized);
        return true;
    }

    private static bool TryFindArray(
        JsonElement element,
        out JsonElement array,
        params string[] preferredNames)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            array = element;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in preferredNames)
            {
                if (TryGetProperty(element, name, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                {
                    array = candidate;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindArray(property.Value, out array, preferredNames))
                {
                    return true;
                }
            }
        }

        array = default;
        return false;
    }

    private static string? TryFindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetRawText();
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = TryFindString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static double? TryFindDouble(JsonElement? element, params string[] names) =>
        element.HasValue ? TryFindDouble(element.Value, names) : null;

    private static double? TryFindDouble(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = TryFindDouble(property.Value, names);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool? TryFindBoolean(JsonElement? element, params string[] names)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetProperty(element.Value, name, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static bool? TryFindNestedBoolean(JsonElement element, params string[] names)
    {
        var direct = TryFindBoolean(element, names);
        if (direct.HasValue || element.ValueKind != JsonValueKind.Object)
        {
            return direct;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nested = TryFindNestedBoolean(property.Value, names);
            if (nested.HasValue)
            {
                return nested;
            }
        }

        return null;
    }

    private static double? TryFindTime(JsonElement element, params string[] names)
    {
        var raw = TryFindDouble(element, names);
        if (raw.HasValue)
        {
            return raw.Value > 10_000 ? raw.Value / 1000 : raw.Value;
        }

        var text = TryFindString(element, names);
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.TotalSeconds;
        }

        return null;
    }

    internal static string? TryFindArtworkUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "artwork", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var url = TryFindString(property.Value, "url", "artworkURL", "artworkUrl", "imageUrl");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nestedUrl = TryFindArtworkUrl(property.Value);
            if (!string.IsNullOrWhiteSpace(nestedUrl))
            {
                return nestedUrl;
            }
        }

        return TryFindString(element, "artworkURL", "artworkUrl", "imageUrl");
    }

    private static int? TryFindCurrentIndex(JsonElement root)
    {
        var value = TryFindDouble(root, "currentIndex", "current", "position", "queuePosition");
        return value.HasValue ? (int)value.Value : null;
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

public enum CiderConnectionState
{
    Connected,
    Offline,
    Unauthorized,
    Error
}

public sealed record CiderResult(
    CiderConnectionState State,
    NowPlayingInfo? Track,
    string Message)
{
    public static CiderResult Connected(NowPlayingInfo? track) =>
        new(CiderConnectionState.Connected, track, AppText.Get("已连接 Cider", "Connected to Cider"));

    public static CiderResult Offline(string message) =>
        new(CiderConnectionState.Offline, null, message);

    public static CiderResult Unauthorized(string message) =>
        new(CiderConnectionState.Unauthorized, null, message);

    public static CiderResult Error(string message) =>
        new(CiderConnectionState.Error, null, message);
}
