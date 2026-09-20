using Lyrider.Models;

namespace Lyrider.Services;

/// <summary>
/// Pure layout math for the lyrics panel. Kept free of WinUI types so the tests
/// project (net10.0, no Windows App SDK) can link and cover it.
/// </summary>
internal static class LyricPresentation
{
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
}

public readonly record struct LyricLineState(double Opacity, double Scale);
