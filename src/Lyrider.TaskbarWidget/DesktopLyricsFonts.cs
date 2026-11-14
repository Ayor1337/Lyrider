using System.Globalization;
using System.Windows.Markup;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace Lyrider.TaskbarWidget;

/// <summary>Name 是保存到设置里的英文族名，DisplayName 是按界面语言显示的名称。</summary>
public sealed record DesktopLyricsFontChoice(string Name, string DisplayName);

public static class DesktopLyricsFonts
{
    public const string DefaultFamily = "Segoe UI";

    private static IReadOnlyList<DesktopLyricsFontChoice>? _installed;

    public static IReadOnlyList<DesktopLyricsFontChoice> GetInstalled() => _installed ??= Load();

    private static DesktopLyricsFontChoice[] Load()
    {
        var english = XmlLanguage.GetLanguage("en-us");
        var display = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        string NameIn(FontFamily family, XmlLanguage language, string fallback) =>
            family.FamilyNames.TryGetValue(language, out var name) && !string.IsNullOrWhiteSpace(name) ? name : fallback;
        return Fonts.SystemFontFamilies
            .Select(family =>
            {
                var name = NameIn(family, english, family.Source);
                return new DesktopLyricsFontChoice(name, NameIn(family, display, name));
            })
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Name) && !choice.Name.StartsWith('@'))
            .DistinctBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
