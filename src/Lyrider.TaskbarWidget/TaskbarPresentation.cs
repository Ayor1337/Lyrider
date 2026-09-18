namespace Lyrider.TaskbarWidget;

public static class TaskbarPresentation
{
    public static bool ShouldShow(TaskbarPlaybackState state) =>
        state.IsAvailable && !string.IsNullOrWhiteSpace(state.Title);

    public static string GetPlayPauseGlyph(bool isPlaying) =>
        isPlaying ? "\uE769" : "\uE768";

    public static TaskbarDisplayText GetDisplayText(TaskbarPlaybackState state)
    {
        if (!string.IsNullOrWhiteSpace(state.CurrentLyric))
        {
            return new TaskbarDisplayText(
                state.CurrentLyric.Trim(),
                state.NextLyric?.Trim() ?? string.Empty);
        }

        return new TaskbarDisplayText(state.Title, state.Artist);
    }

    public static double CalculateMarqueeDistance(double contentWidth, double viewportWidth) =>
        Math.Max(0, contentWidth - viewportWidth);

    public static double CalculateMarqueeCycleDistance(double contentWidth, double gap) =>
        Math.Max(0, contentWidth) + Math.Max(0, gap);

    public static int GetLyricTransitionDirection(
        TaskbarPlaybackState previous,
        TaskbarPlaybackState current)
    {
        if (previous.CurrentLyricIndex is not int previousIndex ||
            current.CurrentLyricIndex is not int currentIndex ||
            previousIndex == currentIndex ||
            string.IsNullOrWhiteSpace(previous.CurrentLyric) ||
            string.IsNullOrWhiteSpace(current.CurrentLyric) ||
            !string.Equals(previous.Title, current.Title, StringComparison.Ordinal) ||
            !string.Equals(previous.Artist, current.Artist, StringComparison.Ordinal))
        {
            return 0;
        }

        return currentIndex > previousIndex ? 1 : -1;
    }
}
