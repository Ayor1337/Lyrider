namespace Lyrider.Services;

internal static class RepeatPresentation
{
    public static RepeatDisplayState ForMode(int mode) =>
        mode switch
        {
            1 => new RepeatDisplayState("循环：单曲循环", true, 1),
            2 => new RepeatDisplayState("循环：列表循环", false, 1),
            _ => new RepeatDisplayState("循环：关闭", false, 0.55)
        };
}

internal readonly record struct RepeatDisplayState(
    string Label,
    bool ShowOneBadge,
    double IconOpacity);
