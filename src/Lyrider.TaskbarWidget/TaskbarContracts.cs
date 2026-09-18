namespace Lyrider.TaskbarWidget;

public sealed record TaskbarPlaybackState(
    string Title,
    string Artist,
    string? ArtworkUrl,
    bool IsPlaying,
    bool IsAvailable,
    string? CurrentLyric = null,
    string? NextLyric = null)
{
    public static TaskbarPlaybackState Unavailable { get; } = new(
        string.Empty,
        string.Empty,
        null,
        false,
        false);
}

public sealed record TaskbarDisplayText(
    string Primary,
    string Secondary);

public enum TaskbarPlaybackCommand
{
    Previous,
    TogglePlayPause,
    Next
}
