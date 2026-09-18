namespace Lyrider.TaskbarWidget;

public static class TaskbarPresentation
{
    public static bool ShouldShow(TaskbarPlaybackState state) =>
        state.IsAvailable && !string.IsNullOrWhiteSpace(state.Title);

    public static string GetPlayPauseGlyph(bool isPlaying) =>
        isPlaying ? "\uE769" : "\uE768";
}
