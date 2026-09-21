namespace Lyrider.Models;

public sealed record PlaybackStatus(bool IsPlaying, double Volume);

public sealed record QueueSnapshot(
    IReadOnlyList<QueueItemInfo> Items,
    int CurrentIndex);

public sealed class QueueItemInfo(
    int index,
    string? id,
    string name,
    string artistName,
    string albumName,
    double durationInMillis,
    string? artworkUrl,
    bool hasLyrics = false,
    bool hasTimeSyncedLyrics = false)
{
    public int Index { get; set; } = index;

    public string? Id { get; } = id;

    public string Name { get; } = name;

    public string ArtistName { get; } = artistName;

    public string AlbumName { get; } = albumName;

    public double DurationInMillis { get; } = durationInMillis;

    public string? ArtworkUrl { get; } = artworkUrl;

    public bool HasLyrics { get; } = hasLyrics;

    public bool HasTimeSyncedLyrics { get; } = hasTimeSyncedLyrics;

    public string DurationText
    {
        get
        {
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, DurationInMillis));
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }
    }
}

public sealed record LyricLineInfo(
    double StartTime,
    double? EndTime,
    string Text,
    string? Translation = null);

public enum LyricsSource
{
    Auto,
    Cider,
    Netease,
    QqMusic,
    Musixmatch,
    Lrclib
}

public sealed record LyricsSnapshot(
    IReadOnlyList<LyricLineInfo> Lines,
    bool IsTimeSynced,
    LyricsSource Source)
{
    public static LyricsSnapshot Empty { get; } = new([], false, LyricsSource.Auto);
}
