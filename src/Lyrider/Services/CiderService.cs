using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lyrider.Models;

namespace Lyrider.Services;

public sealed class CiderService : IDisposable
{
    private static readonly Uri NowPlayingUri = new("api/v1/playback/now-playing", UriKind.Relative);
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public CiderService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:10767/"),
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task<CiderResult> GetNowPlayingAsync(
        string? appToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NowPlayingUri);

            if (!string.IsNullOrWhiteSpace(appToken))
            {
                request.Headers.TryAddWithoutValidation("apptoken", appToken);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return CiderResult.Unauthorized("Token 缺失或无效");
            }

            if (!response.IsSuccessStatusCode)
            {
                return CiderResult.Error($"Cider API 返回 {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadFromJsonAsync<CiderNowPlayingResponse>(
                _jsonOptions,
                cancellationToken);

            if (payload is null)
            {
                return CiderResult.Error("Cider API 返回了空响应");
            }

            if (!string.IsNullOrWhiteSpace(payload.Status) &&
                !string.Equals(payload.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return CiderResult.Error("Cider API 返回了错误状态");
            }

            return CiderResult.Connected(payload.Info);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CiderResult.Offline("连接 Cider 超时");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return CiderResult.Offline("无法连接 Cider，请确认应用已运行");
        }
        catch (JsonException)
        {
            return CiderResult.Error("无法解析 Cider 返回的数据");
        }
        catch (Exception)
        {
            return CiderResult.Error("读取 Cider 状态时发生错误");
        }
    }

    public void Dispose() => _httpClient.Dispose();
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
        new(CiderConnectionState.Connected, track, "已连接 Cider");

    public static CiderResult Offline(string message) =>
        new(CiderConnectionState.Offline, null, message);

    public static CiderResult Unauthorized(string message) =>
        new(CiderConnectionState.Unauthorized, null, message);

    public static CiderResult Error(string message) =>
        new(CiderConnectionState.Error, null, message);
}
