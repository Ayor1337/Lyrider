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
    public string AccessibleName
    {
        get
        {
            var name = AppText.Format("{0}，{1}，{2}", "{0}, {1}, {2}", Name, ArtistName, DurationText);
            return LyricsKindText is { } lyricsKind
                ? AppText.Format("{0}，{1}", "{0}, {1}", name, lyricsKind)
                : name;
        }
    }

    /// <summary>The lyrics kind shown when hovering the row's lyrics icon; null without lyrics.</summary>
    public string? LyricsKindText => HasTimeSyncedLyrics
        ? AppText.Get("逐行歌词", "Synced lyrics")
        : HasPlainLyricsOnly
            ? AppText.Get("普通歌词", "Plain lyrics")
            : null;

    /// <summary>Artist and album, or the artist alone when Cider has no album name.</summary>
    public string SubtitleText => string.IsNullOrWhiteSpace(AlbumName) || AlbumName == "—"
        ? ArtistName
        : $"{ArtistName} · {AlbumName}";

    public bool HasPlainLyricsOnly => HasLyrics && !HasTimeSyncedLyrics;

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

public static class QueueSummary
{
    /// <summary>Describes the upcoming queue, e.g. "6 首 · 约 23 分钟".</summary>
    public static string Format(IReadOnlyCollection<QueueItemInfo> items)
    {
        var count = items.Count;
        var countText = AppText.IsChinese
            ? $"{count} 首"
            : count == 1 ? "1 song" : $"{count} songs";
        var totalMinutes = (int)Math.Round(items.Sum(item => Math.Max(0, item.DurationInMillis)) / 60000d);
        if (count == 0 || totalMinutes <= 0)
        {
            return countText;
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        string durationText;
        if (hours == 0)
        {
            durationText = AppText.Format("约 {0} 分钟", "about {0} min", minutes);
        }
        else if (minutes == 0)
        {
            durationText = AppText.Format("约 {0} 小时", "about {0} hr", hours);
        }
        else
        {
            durationText = AppText.Format("约 {0} 小时 {1} 分钟", "about {0} hr {1} min", hours, minutes);
        }

        return $"{countText} · {durationText}";
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
