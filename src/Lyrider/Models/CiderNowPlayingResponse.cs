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

    [JsonPropertyName("playParams")]
    public PlayParameters? PlayParameters { get; init; }

    [JsonPropertyName("shuffleMode")]
    public int ShuffleMode { get; init; }

    [JsonPropertyName("repeatMode")]
    public int RepeatMode { get; init; }

    [JsonPropertyName("hasLyrics")]
    public bool HasLyrics { get; init; }

    [JsonPropertyName("hasTimeSyncedLyrics")]
    public bool HasTimeSyncedLyrics { get; init; }
}

public sealed class ArtworkInfo
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("bgColor")]
    public string? BackgroundColor { get; init; }
}

public sealed class PlayParameters
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }
}
