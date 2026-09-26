namespace Lyrider.TaskbarWidget;

internal sealed class TaskbarPlacementAnimation
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);
    private PixelPoint? _target;
    private double _startX;
    private TimeSpan _startedAt;

    public bool IsAnimating { get; private set; }

    public void MoveTo(PixelPoint target, TimeSpan now, bool animate, int? maximumX = null)
    {
        var originalX = GetCurrentX(now);
        var currentX = originalX;
        if (maximumX is int boundary)
        {
            // Large tray expansions must not leave the widget covering system icons while it animates.
            currentX = currentX is double x ? Math.Min(x, boundary) : null;
            target = target with { X = Math.Min(target.X, boundary) };
        }
        if (animate && _target == target && currentX == originalX)
        {
            return;
        }

        _startX = currentX ?? target.X;
        _target = target;
        _startedAt = now;
        IsAnimating = animate && _startX != target.X;
    }

    public PixelPoint? GetPosition(TimeSpan now) =>
        GetCurrentX(now) is double x && _target is PixelPoint target
            ? new PixelPoint((int)Math.Round(x), target.Y)
            : null;

    private double? GetCurrentX(TimeSpan now)
    {
        if (_target is not PixelPoint target)
        {
            return null;
        }
        if (!IsAnimating)
        {
            return target.X;
        }

        var progress = Math.Clamp((now - _startedAt).TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
        if (progress >= 1)
        {
            IsAnimating = false;
            return target.X;
        }

        var remaining = 1 - progress;
        var eased = 1 - remaining * remaining * remaining;
        return _startX + (target.X - _startX) * eased;
    }

    public void Reset()
    {
        _target = null;
        IsAnimating = false;
    }
}
