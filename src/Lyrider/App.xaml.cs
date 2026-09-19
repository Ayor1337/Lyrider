using System.Globalization;
using Lyrider.Services;
using Microsoft.UI.Xaml;
using Windows.System.UserProfile;
using ApplicationLanguages = Microsoft.Windows.Globalization.ApplicationLanguages;

namespace Lyrider;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        ApplyLanguage(new SettingsStore().Load().Language);
        InitializeComponent();
    }

    private static void ApplyLanguage(string language)
    {
        var languageTag = language is "zh-CN" or "en-US" ? language : string.Empty;
        if (languageTag.Length > 0)
        {
            ApplicationLanguages.PrimaryLanguageOverride = languageTag;
        }

        var cultureName = languageTag.Length > 0
            ? languageTag
            : GlobalizationPreferences.Languages.FirstOrDefault() ?? "en-US";
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.GetCultureInfo("en-US");
        }

        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
