namespace Lyrider.Services;

internal static class RepeatPresentation
{
    public static RepeatDisplayState ForMode(int mode) =>
        mode switch
        {
            1 => new RepeatDisplayState(AppText.Get("循环：单曲循环", "Repeat: One"), true, 1),
            2 => new RepeatDisplayState(AppText.Get("循环：列表循环", "Repeat: All"), false, 1),
            _ => new RepeatDisplayState(AppText.Get("循环：关闭", "Repeat: Off"), false, 0.55)
        };
}

internal readonly record struct RepeatDisplayState(
    string Label,
    bool ShowOneBadge,
    double IconOpacity);
