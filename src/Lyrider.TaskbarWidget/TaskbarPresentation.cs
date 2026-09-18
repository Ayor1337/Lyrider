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
}
