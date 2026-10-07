using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Lyrider.TaskbarWidget;

public enum DesktopLyricsDisplayMode
{
    Translation,
    SingleLine,
    NextLine
}

public enum DesktopLyricsLayout
{
    Vertical,
    Horizontal
}

public enum DesktopLyricsCommand
{
    ToggleEnabled,
    ToggleLocked,
    OpenSettings,
    ResetPosition
}

public sealed record DesktopLyricsPosition(string Monitor, double CenterRatio, double BottomRatio, double Width,
    double? Height = null);

public sealed record DesktopLyricsOptions(
    bool Enabled = false,
    bool Locked = false,
    DesktopLyricsDisplayMode DisplayMode = DesktopLyricsDisplayMode.Translation,
    double FontSize = 32,
    string TextColor = "#FFFFFF",
    double BackgroundOpacity = 0,
    bool HideWhenPaused = false,
    DesktopLyricsPosition? Position = null,
    bool KaraokeEnabled = false,
    string HighlightColor = "#4FDFFF",
    bool? DoubleLineEnabled = null,
    bool? TranslationEnabled = null,
    DesktopLyricsLayout Layout = DesktopLyricsLayout.Vertical)
{
    // 新开关缺失时沿用旧显示模式，保留已有配置。
    [JsonIgnore]
    public bool ShowDoubleLine => DoubleLineEnabled ?? (DisplayMode != DesktopLyricsDisplayMode.SingleLine);

    [JsonIgnore]
    public bool ShowTranslation => TranslationEnabled ?? (DisplayMode == DesktopLyricsDisplayMode.Translation);

    public DesktopLyricsOptions Normalize() => this with
    {
        DisplayMode = Enum.IsDefined(DisplayMode) ? DisplayMode : DesktopLyricsDisplayMode.Translation,
        Layout = Enum.IsDefined(Layout) ? Layout : DesktopLyricsLayout.Vertical,
        FontSize = ClampFinite(FontSize, 16, 72, 32),
        TextColor = IsColor(TextColor) ? TextColor : "#FFFFFF",
        HighlightColor = IsColor(HighlightColor) ? HighlightColor : "#4FDFFF",
        BackgroundOpacity = ClampFinite(BackgroundOpacity, 0, 1, 0),
        Position = Position is null ? null : Position with
        {
            CenterRatio = ClampFinite(Position.CenterRatio, 0, 1, 0.5),
            BottomRatio = ClampFinite(Position.BottomRatio, 0, 1, 0),
            Width = ClampFinite(Position.Width, DesktopLyricsPlacement.MinimumWidth, DesktopLyricsPlacement.MaximumWidth, 960),
            Height = Position.Height is { } height && double.IsFinite(height) && height > 0
                ? Math.Clamp(height, DesktopLyricsPlacement.MinimumHeight, DesktopLyricsPlacement.MaximumHeight) : null
        }
    };

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value.AsSpan(1).ToArray().All(Uri.IsHexDigit);
}

public sealed record DesktopLyricsState(
    string Title,
    bool IsAvailable,
    bool IsPlaying,
    string? CurrentLyric = null,
    string? Translation = null,
    string? NextLyric = null,
    DesktopLyricsTiming? Timing = null,
    int LineOrdinal = 0)
{
    public static DesktopLyricsState Unavailable { get; } = new(string.Empty, false, false);
}

public sealed record DesktopLyricsWord(double Start, double End, double From, double To);

public sealed record DesktopLyricsTiming(ImmutableArray<DesktopLyricsWord> Words, double Position, long ObservedTimestamp)
{
    public double ProgressAt(double elapsedSeconds, bool isPlaying)
    {
        if (Words.IsDefaultOrEmpty || !double.IsFinite(Position)) return 0;
        var elapsed = isPlaying && double.IsFinite(elapsedSeconds) ? Math.Max(0, elapsedSeconds) : 0;
        var position = Position + elapsed;
        var progress = 0.0;
        foreach (var word in Words)
        {
            if (position < word.Start) return progress;
            if (position < word.End)
                return word.From + (word.To - word.From) * Math.Clamp((position - word.Start) / (word.End - word.Start), 0, 1);
            progress = word.To;
        }
        return 1;
    }
}

public sealed record DesktopLyricsDisplayText(string Primary, string? Secondary, bool IsVisible,
    bool CurrentInSecondary = false);

public static class DesktopLyricsPresentation
{
    public static DesktopLyricsDisplayText Select(DesktopLyricsState state, DesktopLyricsOptions options)
    {
        var visible = options.Enabled && state.IsAvailable && (!options.HideWhenPaused || state.IsPlaying);
        if (string.IsNullOrWhiteSpace(state.CurrentLyric))
        {
            return new(state.Title, null, visible);
        }

        var hasTranslation = options.ShowTranslation && !string.IsNullOrWhiteSpace(state.Translation);
        var secondary = options.ShowDoubleLine
            ? hasTranslation ? state.Translation : state.NextLyric
            : null;
        if (options.ShowDoubleLine && !hasTranslation && (state.LineOrdinal & 1) == 1)
            return new(string.IsNullOrWhiteSpace(secondary) ? string.Empty : secondary, state.CurrentLyric, visible, true);
        return new(state.CurrentLyric, string.IsNullOrWhiteSpace(secondary) ? null : secondary, visible);
    }
}
