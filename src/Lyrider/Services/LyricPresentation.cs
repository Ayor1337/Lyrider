using System.Globalization;
using Lyrider.Models;

namespace Lyrider.Services;

/// <summary>
/// Pure layout math for the lyrics panel. Kept free of WinUI types so the tests
/// project (net10.0, no Windows App SDK) can link and cover it.
/// </summary>
internal static class LyricPresentation
{
    private static readonly string[] LeadingCreditLabels =
    [
        "歌名", "歌手", "演唱", "主唱", "作词", "作詞", "填词", "填詞", "作曲", "编曲", "編曲", "制作人", "製作人",
        "title", "artist", "singer", "vocals", "lyrics", "lyricist", "composer", "arranger", "producer"
    ];

    public const double ActiveScale = 1.08;
    public const double InactiveScale = 1.0;
    public const double ActiveOpacity = 1.0;
    public const double AdjacentOpacity = 0.58;
    public const double FarOpacity = 0.35;
    public const double UnsyncedOpacity = 0.72;
    public const double HoverOpacity = 0.88;
    public const int AdjacentDistance = 1;
    public const double AnchorRatio = 0.35;
    public const double DefaultViewportHeight = 420;

    public static int FindActiveLineIndex(IReadOnlyList<LyricLineInfo> lines, double playbackTime)
    {
        var activeIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].StartTime > playbackTime)
            {
                break;
            }

            activeIndex = index;
        }

        return activeIndex;
    }

    public static int FindFirstTaskbarLyricIndex(
        IReadOnlyList<LyricLineInfo> lines,
        string? title,
        string? artist)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!IsLeadingNonLyricLine(lines[index].Text, title, artist))
            {
                return index;
            }
        }

        return lines.Count;
    }

    public static TimeSpan? DelayUntilNextLine(
        IReadOnlyList<LyricLineInfo> lines,
        double playbackTime)
    {
        foreach (var line in lines)
        {
            if (line.StartTime > playbackTime)
            {
                return TimeSpan.FromSeconds(line.StartTime - playbackTime);
            }
        }

        return null;
    }

    public static LyricLineState StateForIndex(int index, int activeIndex, bool isTimeSynced)
    {
        if (!isTimeSynced)
        {
            return new LyricLineState(UnsyncedOpacity, InactiveScale);
        }

        return activeIndex < 0
            ? new LyricLineState(FarOpacity, InactiveScale)
            : StateForDistance(Math.Abs(index - activeIndex));
    }

    public static LyricLineState StateForDistance(int distance) =>
        distance switch
        {
            0 => new LyricLineState(ActiveOpacity, ActiveScale),
            AdjacentDistance => new LyricLineState(AdjacentOpacity, InactiveScale),
            _ => new LyricLineState(FarOpacity, InactiveScale)
        };

    /// <summary>
    /// Pointer feedback brightens the line itself; the active line keeps its scale, so it
    /// stays distinguishable from a merely hovered neighbour.
    /// </summary>
    public static LyricLineState WithHover(LyricLineState state, bool hovered) =>
        hovered && state.Opacity < HoverOpacity
            ? state with { Opacity = HoverOpacity }
            : state;

    /// <summary>
    /// Offset that parks the active line's visual centre at <paramref name="anchorRatio"/>
    /// of the viewport. <paramref name="lineTopInContent"/> is measured within the scrolled
    /// content, so the offset falls straight out of it: scrolling to <c>centre - viewport * ratio</c>
    /// leaves the centre exactly at that ratio.
    /// </summary>
    public static double ComputeScrollOffset(
        double lineTopInContent,
        double lineHeight,
        double scale,
        double viewportHeight,
        double extentHeight,
        double anchorRatio = AnchorRatio)
    {
        var maxOffset = Math.Max(0, extentHeight - viewportHeight);
        var lineCentreInContent = lineTopInContent + (lineHeight * scale / 2);
        return Math.Clamp(lineCentreInContent - (viewportHeight * anchorRatio), 0, maxOffset);
    }

    public static double TopGutter(double viewportHeight) =>
        Math.Max(0, EffectiveViewport(viewportHeight) * AnchorRatio);

    public static double BottomGutter(double viewportHeight) =>
        Math.Max(0, EffectiveViewport(viewportHeight) * (1 - AnchorRatio));

    public static int ComputeLyricsSignature(
        IReadOnlyList<LyricLineInfo> lines,
        double fontSize,
        bool isTimeSynced,
        bool convertTraditionalToSimplified = false,
        bool showTranslation = false)
    {
        var hash = new HashCode();
        hash.Add(lines.Count);
        hash.Add(fontSize);
        hash.Add(isTimeSynced);
        hash.Add(convertTraditionalToSimplified);
        hash.Add(showTranslation);
        foreach (var line in lines)
        {
            hash.Add(line.StartTime);
            hash.Add(line.EndTime);
            hash.Add(line.Text, StringComparer.Ordinal);
            hash.Add(line.Translation, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static double EffectiveViewport(double viewportHeight) =>
        viewportHeight > 0 ? viewportHeight : DefaultViewportHeight;

    private static bool IsLeadingNonLyricLine(string text, string? title, string? artist)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 ||
            EqualsTrackMetadata(trimmed, title) ||
            EqualsTrackMetadata(trimmed, artist) ||
            IsInstrumentalMarker(trimmed) ||
            IsDecorationOnly(trimmed))
        {
            return true;
        }

        foreach (var label in LeadingCreditLabels)
        {
            if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Length == label.Length || IsCreditSeparator(trimmed[label.Length]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsTrackMetadata(string text, string? metadata) =>
        !string.IsNullOrWhiteSpace(metadata) &&
        string.Equals(text, metadata.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsInstrumentalMarker(string text) =>
        string.Equals(text, "纯音乐", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "純音樂", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text, "instrumental", StringComparison.OrdinalIgnoreCase);

    private static bool IsCreditSeparator(char character) =>
        char.IsWhiteSpace(character) || character is ':' or '：' or '-' or '—' or '–' or '·' or '/' or '|';

    private static bool IsDecorationOnly(string text)
    {
        foreach (var character in text)
        {
            var category = char.GetUnicodeCategory(character);
            if (!char.IsWhiteSpace(character) && category is not (
                UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.DashPunctuation or
                UnicodeCategory.OpenPunctuation or
                UnicodeCategory.ClosePunctuation or
                UnicodeCategory.InitialQuotePunctuation or
                UnicodeCategory.FinalQuotePunctuation or
                UnicodeCategory.OtherPunctuation or
                UnicodeCategory.MathSymbol or
                UnicodeCategory.OtherSymbol))
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct LyricLineState(double Opacity, double Scale);
