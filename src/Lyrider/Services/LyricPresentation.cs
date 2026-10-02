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

    private static readonly HashSet<string> InstrumentalMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "纯音乐", "純音樂", "instrumental", "伴奏", "间奏", "間奏", "尾奏", "interlude", "outro",
        "纯音乐请欣赏", "純音樂請欣賞", "纯音乐请您欣赏", "純音樂請您欣賞",
        "此歌曲为没有填词的纯音乐", "此歌曲為沒有填詞的純音樂",
        "此歌曲为没有填词的纯音乐请欣赏", "此歌曲為沒有填詞的純音樂請欣賞",
        "此歌曲为没有填词的纯音乐请您欣赏", "此歌曲為沒有填詞的純音樂請您欣賞"
    };

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

    public static int FindTaskbarLyricIndex(
        IReadOnlyList<LyricLineInfo> lines,
        int activeIndex,
        string? title,
        string? artist)
    {
        var firstIndex = FindFirstTaskbarLyricIndex(lines, title, artist);
        if (activeIndex < firstIndex || activeIndex >= lines.Count)
        {
            return -1;
        }

        if (!IsTaskbarGap(lines[activeIndex].Text))
        {
            return activeIndex;
        }

        // 间奏预览下一句，尾奏保留最后一句；原始时间轴仍供主窗口使用。
        var nextIndex = FindNextTaskbarLyricIndex(lines, activeIndex);
        if (nextIndex >= 0)
        {
            return nextIndex;
        }

        for (var index = activeIndex - 1; index >= firstIndex; index--)
        {
            if (!IsTaskbarGap(lines[index].Text))
            {
                return index;
            }
        }

        return -1;
    }

    public static int FindNextTaskbarLyricIndex(IReadOnlyList<LyricLineInfo> lines, int currentIndex)
    {
        if (currentIndex < 0 || currentIndex >= lines.Count)
        {
            return -1;
        }

        for (var index = currentIndex + 1; index < lines.Count; index++)
        {
            if (!IsTaskbarGap(lines[index].Text))
            {
                return index;
            }
        }

        return -1;
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
        if (IsTaskbarGap(trimmed) ||
            EqualsTrackMetadata(trimmed, title) ||
            EqualsTrackMetadata(trimmed, artist))
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

    private static bool IsTaskbarGap(string text) =>
        string.IsNullOrWhiteSpace(text) || IsInstrumentalMarker(text) || IsDecorationOnly(text);

    private static bool IsInstrumentalMarker(string text)
    {
        var normalized = string.Concat(text.Where(character =>
            !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
        return InstrumentalMarkers.Contains(normalized);
    }

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
