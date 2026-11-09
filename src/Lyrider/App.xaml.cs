using System.Globalization;
using Lyrider.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.System.UserProfile;
using ApplicationLanguages = Microsoft.Windows.Globalization.ApplicationLanguages;

namespace Lyrider;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Lyrider.Instance";

    private Window? _window;
    private Mutex? _instanceMutex;

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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Held for the process lifetime; only a protocol launch defers to a running instance, so
        // the Cider plugin opening lyrider:// never starts a second copy.
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance && IsProtocolActivation())
        {
            Exit();
            return;
        }

        var window = new MainWindow();
        _window = window;
        await window.StartAsync();
    }

    private static bool IsProtocolActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.Protocol;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
