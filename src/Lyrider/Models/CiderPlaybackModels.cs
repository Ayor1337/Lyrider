namespace Lyrider.Models;

public sealed record PlaybackStatus(bool IsPlaying, double Volume);

public sealed record QueueSnapshot(
    IReadOnlyList<QueueItemInfo> Items,
    int CurrentIndex);

public sealed record QueueItemInfo(
    int Index,
    string? Id,
    string Name,
    string ArtistName,
    string AlbumName,
    double DurationInMillis,
    string? ArtworkUrl)
{
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
    string Text);
