using Lyrider.Models;
using Lyrider.TaskbarWidget;
using System.Collections.Immutable;
using System.Globalization;

namespace Lyrider.Services;

internal static class DesktopLyricStateBuilder
{
    public static DesktopLyricsState Build(string title, string artist, IReadOnlyList<LyricLineInfo> lines,
        int activeIndex, bool timeSynced, bool isPlaying, bool convertToSimplified,
        double playbackPosition = 0, long observedTimestamp = 0)
    {
        var index = timeSynced ? LyricPresentation.FindTaskbarLyricIndex(lines, activeIndex, title, artist) : -1;
        string Display(string text) => convertToSimplified ? ChineseTextConverter.ToSimplified(text) : text;
        if (index < 0)
        {
            return new(title, true, isPlaying);
        }

        var nextIndex = LyricPresentation.FindNextTaskbarLyricIndex(lines, index);
        var translation = lines[index].Translation;
        var line = lines[index];
        var text = Display(line.Text);
        var words = MapWords(line, text);
        var timing = words.IsDefaultOrEmpty ? null : new DesktopLyricsTiming(words, playbackPosition, observedTimestamp);
        var ordinal = 0;
        for (var lyricIndex = LyricPresentation.FindFirstTaskbarLyricIndex(lines, title, artist);
            lyricIndex >= 0 && lyricIndex < index;
            lyricIndex = LyricPresentation.FindNextTaskbarLyricIndex(lines, lyricIndex))
            ordinal++;
        return new(title, true, isPlaying, text,
            string.IsNullOrWhiteSpace(translation) ? null : ChineseTextConverter.ToSimplified(translation),
            nextIndex < 0 ? null : Display(lines[nextIndex].Text), timing, ordinal);
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
