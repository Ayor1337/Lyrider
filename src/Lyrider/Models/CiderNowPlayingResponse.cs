using System.Text.Json.Serialization;

namespace Lyrider.Models;

public sealed class CiderNowPlayingResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("info")]
    public NowPlayingInfo? Info { get; init; }
}

public sealed class NowPlayingInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    [JsonPropertyName("currentPlaybackTime")]
    public double CurrentPlaybackTime { get; init; }

    [JsonPropertyName("durationInMillis")]
    public double DurationInMillis { get; init; }

    [JsonPropertyName("artwork")]
    public ArtworkInfo? Artwork { get; init; }
}

public sealed class ArtworkInfo
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}
