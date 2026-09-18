namespace Lyrider.TaskbarWidget;

public sealed record TaskbarPlaybackState(
    string Title,
    string Artist,
    string? ArtworkUrl,
    bool IsPlaying,
    bool IsAvailable)
{
    public static TaskbarPlaybackState Unavailable { get; } = new(
        string.Empty,
        string.Empty,
        null,
        false,
        false);
}

public enum TaskbarPlaybackCommand
{
    Previous,
    TogglePlayPause,
    Next
}
