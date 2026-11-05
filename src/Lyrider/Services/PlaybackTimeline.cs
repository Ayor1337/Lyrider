namespace Lyrider.Services;

/// <summary>
/// Projects the playback position between server samples. Cider's REST position is a
/// snapshot refreshed about once a second, so a poll may return a value up to a second
/// old; the socket's time events carry their own sample time and are exact. While a
/// precise sample is recent, a REST sample that only looks stale is ignored, and one that
/// disagrees (seek, pause) still wins.
/// </summary>
internal sealed class PlaybackTimeline
{
    private static readonly TimeSpan PreciseSampleLifetime = TimeSpan.FromSeconds(3);
    private const double MaxSnapshotAgeSeconds = 1.5;
    private const double AheadToleranceSeconds = 0.25;

    private double _position;
    private double _duration;
    private bool _isPlaying;
    private TimeSpan _observedAt;
    private TimeSpan? _preciseObservedAt;

    public void Synchronize(
        double position,
        double duration,
        bool isPlaying,
        TimeSpan observedAt)
    {
        if (IsStaleSnapshot(position, isPlaying, observedAt))
        {
            _duration = Math.Max(0, duration);
            return;
        }

        _preciseObservedAt = null;
        Apply(position, duration, isPlaying, observedAt);
    }

    public void SynchronizePrecise(
        double position,
        double duration,
        bool isPlaying,
        TimeSpan observedAt)
    {
        _preciseObservedAt = observedAt;
        Apply(position, duration, isPlaying, observedAt);
    }

    public double PositionAt(TimeSpan now)
    {
        var elapsed = _isPlaying ? Math.Max(0, (now - _observedAt).TotalSeconds) : 0;
        return Math.Clamp(_position + elapsed, 0, _duration);
    }

    public void Reset()
    {
        _position = 0;
        _duration = 0;
        _isPlaying = false;
        _observedAt = TimeSpan.Zero;
        _preciseObservedAt = null;
    }

    private bool IsStaleSnapshot(double position, bool isPlaying, TimeSpan observedAt)
    {
        if (_preciseObservedAt is not { } preciseAt ||
            observedAt - preciseAt > PreciseSampleLifetime ||
            !isPlaying ||
            !_isPlaying)
        {
            return false;
        }

        var lag = PositionAt(observedAt) - position;
        return lag >= -AheadToleranceSeconds && lag <= MaxSnapshotAgeSeconds;
    }

    private void Apply(double position, double duration, bool isPlaying, TimeSpan observedAt)
    {
        _duration = Math.Max(0, duration);
        _position = Math.Clamp(position, 0, _duration);
        _isPlaying = isPlaying;
        _observedAt = observedAt;
    }
}
