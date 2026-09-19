using System.Globalization;

namespace Lyrider.TaskbarWidget;

internal static class WidgetText
{
    public static string Get(string chinese, string english) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? chinese
            : english;
}
