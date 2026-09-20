namespace Lyrider.Services;

internal sealed class PlaybackTimeline
{
    private double _position;
    private double _duration;
    private bool _isPlaying;
    private TimeSpan _observedAt;

    public void Synchronize(
        double position,
        double duration,
        bool isPlaying,
        TimeSpan observedAt)
    {
        _duration = Math.Max(0, duration);
        _position = Math.Clamp(position, 0, _duration);
        _isPlaying = isPlaying;
        _observedAt = observedAt;
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
    }
}
