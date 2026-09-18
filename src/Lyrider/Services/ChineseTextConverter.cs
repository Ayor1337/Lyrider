using OpenccNetLib;

namespace Lyrider.Services;

internal static class ChineseTextConverter
{
    private static readonly Opencc Converter = new(OpenccConfig.T2S);

    public static string ToSimplified(string text) =>
        string.IsNullOrEmpty(text)
            ? text
            : Converter.Convert(text);
}
