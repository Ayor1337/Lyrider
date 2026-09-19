using System.Globalization;

namespace Lyrider.Services;

internal static class AppText
{
    public static bool IsChinese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string Get(string chinese, string english) => IsChinese ? chinese : english;

    public static string Format(string chinese, string english, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(chinese, english), args);
}
