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

public enum DesktopLyricsTextDirection
{
    Horizontal,
    Vertical
}

public enum DesktopLyricsAlignment
{
    Center,
    Split,
    Left,
    Right
}

public enum DesktopLyricsFontWeight
{
    Normal,
    SemiBold,
    Bold
}

public enum DesktopLyricsCommand
{
    ToggleEnabled,
    ToggleLocked,
    OpenSettings,
    ResetPosition,
    Previous,
    TogglePlayPause,
    Next,
    IncreaseFontSize,
    DecreaseFontSize,
    AlignCenter,
    AlignSplit,
    AlignLeft,
    AlignRight,
    SingleLine,
    DoubleLine,
    HorizontalText,
    VerticalText,
    ToggleTranslation,
    ToggleKaraoke
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
    DesktopLyricsLayout Layout = DesktopLyricsLayout.Vertical,
    DesktopLyricsTextDirection TextDirection = DesktopLyricsTextDirection.Horizontal,
    DesktopLyricsAlignment? Alignment = null,
    DesktopLyricsFontWeight FontWeight = DesktopLyricsFontWeight.SemiBold,
    double StrokeThickness = 4,
    string StrokeColor = "#000000")
{
    // 新开关缺失时沿用旧显示模式，保留已有配置。
    [JsonIgnore]
    public bool ShowDoubleLine => DoubleLineEnabled ?? (DisplayMode != DesktopLyricsDisplayMode.SingleLine);

    [JsonIgnore]
    public bool ShowTranslation => TranslationEnabled ?? (DisplayMode == DesktopLyricsDisplayMode.Translation);

    [JsonIgnore]
    public DesktopLyricsAlignment EffectiveAlignment => Alignment ??
        (Layout == DesktopLyricsLayout.Horizontal ? DesktopLyricsAlignment.Split : DesktopLyricsAlignment.Center);

    public DesktopLyricsOptions ApplyCommand(DesktopLyricsCommand command)
    {
        var options = Normalize();
        return (command switch
        {
            DesktopLyricsCommand.ToggleEnabled => options with { Enabled = !options.Enabled },
            DesktopLyricsCommand.ToggleLocked => options with { Locked = !options.Locked },
            DesktopLyricsCommand.ResetPosition => options with { Position = null },
            DesktopLyricsCommand.IncreaseFontSize => options with { FontSize = options.FontSize + 2 },
            DesktopLyricsCommand.DecreaseFontSize => options with { FontSize = options.FontSize - 2 },
            DesktopLyricsCommand.AlignCenter => options with { Alignment = DesktopLyricsAlignment.Center },
            DesktopLyricsCommand.AlignSplit => options with { Alignment = DesktopLyricsAlignment.Split },
            DesktopLyricsCommand.AlignLeft => options with { Alignment = DesktopLyricsAlignment.Left },
            DesktopLyricsCommand.AlignRight => options with { Alignment = DesktopLyricsAlignment.Right },
            DesktopLyricsCommand.SingleLine => options with { DoubleLineEnabled = false },
            DesktopLyricsCommand.DoubleLine => options with { DoubleLineEnabled = true },
            DesktopLyricsCommand.HorizontalText => options with { TextDirection = DesktopLyricsTextDirection.Horizontal },
            DesktopLyricsCommand.VerticalText => options with { TextDirection = DesktopLyricsTextDirection.Vertical },
            DesktopLyricsCommand.ToggleTranslation => options with { TranslationEnabled = !options.ShowTranslation },
            DesktopLyricsCommand.ToggleKaraoke => options with { KaraokeEnabled = !options.KaraokeEnabled },
            _ => options
        }).Normalize();
    }

    public DesktopLyricsOptions Normalize() => this with
    {
        DisplayMode = Enum.IsDefined(DisplayMode) ? DisplayMode : DesktopLyricsDisplayMode.Translation,
        Layout = Enum.IsDefined(Layout) ? Layout : DesktopLyricsLayout.Vertical,
        TextDirection = Enum.IsDefined(TextDirection) ? TextDirection : DesktopLyricsTextDirection.Horizontal,
        Alignment = Alignment is { } alignment && !Enum.IsDefined(alignment) ? DesktopLyricsAlignment.Center : Alignment,
        FontWeight = Enum.IsDefined(FontWeight) ? FontWeight : DesktopLyricsFontWeight.SemiBold,
        StrokeThickness = ClampFinite(StrokeThickness, 0, 8, 4),
        StrokeColor = IsColor(StrokeColor) ? StrokeColor : "#000000",
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
    int LineOrdinal = 0,
    bool IsChineseSong = false)
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

        var hasTranslation = UsesTranslation(state, options);
        if (hasTranslation) return new(state.CurrentLyric, state.Translation, visible);
        var secondary = options.ShowDoubleLine
            ? state.NextLyric
            : null;
        if (options.ShowDoubleLine && !hasTranslation && (state.LineOrdinal & 1) == 1)
            return new(string.IsNullOrWhiteSpace(secondary) ? string.Empty : secondary, state.CurrentLyric, visible, true);
        return new(state.CurrentLyric, string.IsNullOrWhiteSpace(secondary) ? null : secondary, visible);
    }

    public static bool UsesTranslation(DesktopLyricsState state, DesktopLyricsOptions options) =>
        options.ShowTranslation && !state.IsChineseSong && !string.IsNullOrWhiteSpace(state.CurrentLyric) &&
        !string.IsNullOrWhiteSpace(state.Translation);

    public static DesktopLyricsAlignment ResolveAlignment(DesktopLyricsState state, DesktopLyricsOptions options)
    {
        var alignment = options.EffectiveAlignment;
        return alignment == DesktopLyricsAlignment.Split &&
            (UsesTranslation(state, options) || !options.ShowDoubleLine || string.IsNullOrWhiteSpace(state.CurrentLyric))
            ? DesktopLyricsAlignment.Center : alignment;
    }
}
