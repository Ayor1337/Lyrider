using Lyrider.Models;
using Lyrider.TaskbarWidget;
using System.Collections.Immutable;
using System.Globalization;

namespace Lyrider.Services;

internal static class DesktopLyricStateBuilder
{
    // 只依赖歌词本身的结果按歌词列表引用缓存，避免每次刷新都扫描整份歌词。
    private sealed record LyricsAnalysis(IReadOnlyList<LyricLineInfo> Lines, string Title, string Artist,
        bool SkipLeadingCredits, int[] DisplayIndexes, bool IsChinese, bool IsJapanese);

    private static LyricsAnalysis? _analysis;

    public static DesktopLyricsState Build(string title, string artist, IReadOnlyList<LyricLineInfo> lines,
        int activeIndex, bool timeSynced, bool isPlaying, bool convertToSimplified,
        double playbackPosition = 0, long observedTimestamp = 0, bool skipLeadingCredits = true,
        string? album = null)
    {
        var index = timeSynced
            ? LyricPresentation.FindTaskbarLyricIndex(lines, activeIndex, title, artist, skipLeadingCredits)
            : -1;
        string Display(string text) => convertToSimplified ? ChineseTextConverter.ToSimplified(text) : text;
        string? Info(string? value) => string.IsNullOrWhiteSpace(value) ? null : Display(value.Trim());
        if (index < 0)
        {
            return new(title, true, isPlaying, Artist: Info(artist), Album: Info(album));
        }

        var analysis = Analyze(lines, title, artist, skipLeadingCredits);
        var nextIndex = LyricPresentation.FindNextTaskbarLyricIndex(lines, index);
        var translation = lines[index].Translation;
        var line = lines[index];
        var text = Display(line.Text);
        var words = MapWords(line, text);
        var timing = words.IsDefaultOrEmpty ? null : new DesktopLyricsTiming(words, playbackPosition, observedTimestamp);
        // 序号等于时间轴上排在当前句之前的可显示歌词数。
        var ordinal = Array.BinarySearch(analysis.DisplayIndexes, index);
        if (ordinal < 0) ordinal = ~ordinal;
        return new(title, true, isPlaying, text,
            string.IsNullOrWhiteSpace(translation) ? null : ChineseTextConverter.ToSimplified(translation),
            nextIndex < 0 ? null : Display(lines[nextIndex].Text), timing, ordinal,
            analysis.IsChinese, Info(artist), Info(album), analysis.IsJapanese);
    }

    private static LyricsAnalysis Analyze(IReadOnlyList<LyricLineInfo> lines, string title, string artist,
        bool skipLeadingCredits)
    {
        var cached = Volatile.Read(ref _analysis);
        if (cached is not null && ReferenceEquals(cached.Lines, lines) && cached.Title == title &&
            cached.Artist == artist && cached.SkipLeadingCredits == skipLeadingCredits)
        {
            return cached;
        }

        var displayIndexes = new List<int>();
        for (var lyricIndex = LyricPresentation.FindFirstTaskbarLyricIndex(lines, title, artist, skipLeadingCredits);
            lyricIndex >= 0 && lyricIndex < lines.Count;
            lyricIndex = LyricPresentation.FindNextTaskbarLyricIndex(lines, lyricIndex))
            displayIndexes.Add(lyricIndex);
        var analysis = new LyricsAnalysis(lines, title, artist, skipLeadingCredits, displayIndexes.ToArray(),
            LyricsSelection.IsPrimarilyChinese(new(lines, true, LyricsSource.Auto)), IsPrimarilyJapanese(lines));
        Volatile.Write(ref _analysis, analysis);
        return analysis;
    }

    // 至少五分之一的歌词行含假名时按日文排版，避免偶尔一句日文把整首中文歌改成日文字形。
    internal static bool IsPrimarilyJapanese(IReadOnlyList<LyricLineInfo> lines)
    {
        var textLines = 0;
        var kanaLines = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            textLines++;
            if (line.Text.Any(character => character is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ'))
                kanaLines++;
        }
        return kanaLines > 0 && kanaLines * 5 >= textLines;
    }

    private static ImmutableArray<DesktopLyricsWord> MapWords(LyricLineInfo line, string displayedText)
    {
        if (line.Words is not { Count: > 0 } || line.Text.Length != displayedText.Length ||
            !double.IsFinite(line.StartTime)) return [];
        var elements = StringInfo.ParseCombiningCharacters(displayedText);
        if (elements.Length == 0) return [];
        var mapped = ImmutableArray.CreateBuilder<DesktopLyricsWord>();
        var offset = 0;
        var previousEnd = line.StartTime;
        foreach (var word in line.Words)
        {
            var start = line.Text.IndexOf(word.Text, offset, StringComparison.Ordinal);
            if (word.Text.Length == 0 || start < 0 || !string.IsNullOrWhiteSpace(line.Text[offset..start]) ||
                !double.IsFinite(word.StartTime) || !double.IsFinite(word.EndTime) ||
                word.StartTime < previousEnd || word.EndTime <= word.StartTime) return [];
            var end = start + word.Text.Length;
            var firstElement = Array.BinarySearch(elements, start);
            var lastElement = end == displayedText.Length ? elements.Length : Array.BinarySearch(elements, end);
            if (firstElement < 0 || lastElement < 0) return [];
            mapped.Add(new(word.StartTime, word.EndTime,
                (double)firstElement / elements.Length, (double)lastElement / elements.Length));
            offset = end;
            previousEnd = word.EndTime;
        }
        return string.IsNullOrWhiteSpace(line.Text[offset..]) ? mapped.ToImmutable() : [];
    }
}
