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
                state.SecondaryLyric?.Trim() ?? string.Empty);
        }

        return new TaskbarDisplayText(state.Title, state.Artist);
    }

    public static string? SelectSecondaryLyric(
        bool showTranslation,
        string? translation,
        string? nextLyric) =>
        showTranslation && !string.IsNullOrWhiteSpace(translation)
            ? translation
            : nextLyric;

    public static double CalculateMarqueeDistance(double contentWidth, double viewportWidth) =>
        Math.Max(0, contentWidth - viewportWidth);

    /// <summary>
    /// The mirrored layout only applies to an icon-left taskbar, where the widget anchors to the
    /// notification area instead of the widgets button, so its content should hug the right edge.
    /// </summary>
    public static bool UseRightAlignedLayout(bool rightAlignEnabled, TaskbarAlignment alignment) =>
        rightAlignEnabled && alignment == TaskbarAlignment.Left;

    /// <summary>Horizontal offset that puts text narrower than the viewport against its right edge.</summary>
    public static double CalculateRightAlignedOffset(double contentWidth, double viewportWidth) =>
        Math.Max(0, viewportWidth - contentWidth);

    internal static double CalculateMarqueeContentWidth(
        double primaryWidth,
        double secondaryWidth,
        bool synchronizeSecondary) =>
        Math.Max(0, synchronizeSecondary
            ? Math.Max(primaryWidth, secondaryWidth)
            : primaryWidth);

    internal static bool ShouldSynchronizeMarquee(TaskbarPlaybackState state) =>
        state.SecondaryLyricIsTranslation &&
        !string.IsNullOrWhiteSpace(state.CurrentLyric) &&
        !string.IsNullOrWhiteSpace(state.SecondaryLyric);

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
