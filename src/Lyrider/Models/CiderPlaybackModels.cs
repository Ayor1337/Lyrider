using System.ComponentModel;
using Lyrider.Services;

namespace Lyrider.Models;

public sealed record PlaybackStatus(bool IsPlaying);

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
    bool hasTimeSyncedLyrics = false,
    string? isrc = null) : INotifyPropertyChanged
{
    private bool _isSwitching;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; set; } = index;

    /// <summary>True while Cider is being asked to jump to this item.</summary>
    public bool IsSwitching
    {
        get => _isSwitching;
        set
        {
            if (_isSwitching == value)
            {
                return;
            }

            _isSwitching = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSwitching)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentOpacity)));
        }
    }

    public double ContentOpacity => _isSwitching ? 0.45 : 1;

    /// <summary>What a screen reader announces for the row.</summary>
    public string AccessibleName => AppText.Format(
        "{0}，{1}，{2}",
        "{0}, {1}, {2}",
        Name,
        ArtistName,
        DurationText);

    public string? Isrc { get; } = isrc;

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
    string? Translation = null,
    IReadOnlyList<LyricWordInfo>? Words = null);

public sealed record LyricWordInfo(double StartTime, double EndTime, string Text);

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
