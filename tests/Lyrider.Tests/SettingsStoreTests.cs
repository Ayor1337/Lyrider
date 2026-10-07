using Lyrider.Models;
using Lyrider.Services;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private string _directory = null!;
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Lyrider-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"desktopLyrics\":null}")]
    public void Load_OldOrNullDesktopSettings_UsesDisabledUnlockedDefaults(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var options = new SettingsStore(SettingsPath).Load().DesktopLyrics;
        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.Locked);
        Assert.IsFalse(options.KaraokeEnabled);
        Assert.IsTrue(options.ShowDoubleLine);
        Assert.IsTrue(options.ShowTranslation);
        Assert.AreEqual(DesktopLyricsLayout.Vertical, options.Layout);
        Assert.AreEqual("#4FDFFF", options.HighlightColor);
        Assert.AreEqual(32, options.FontSize);
        Assert.AreEqual(DesktopLyricsDisplayMode.Translation, options.DisplayMode);
    }

    [TestMethod]
    public void Load_InvalidDesktopValues_NormalizesWithoutDiscardingOtherSettings()
    {
        File.WriteAllText(SettingsPath, """{"theme":"Light","desktopLyrics":{"fontSize":100,"textColor":"oops","highlightColor":null,"backgroundOpacity":-1,"displayMode":99,"layout":99}}""");
        var settings = new SettingsStore(SettingsPath).Load();
        Assert.AreEqual("Light", settings.Theme);
        Assert.AreEqual(72, settings.DesktopLyrics.FontSize);
        Assert.AreEqual("#FFFFFF", settings.DesktopLyrics.TextColor);
        Assert.AreEqual("#4FDFFF", settings.DesktopLyrics.HighlightColor);
        Assert.AreEqual(0, settings.DesktopLyrics.BackgroundOpacity);
        Assert.AreEqual(DesktopLyricsDisplayMode.Translation, settings.DesktopLyrics.DisplayMode);
        Assert.AreEqual(DesktopLyricsLayout.Vertical, settings.DesktopLyrics.Layout);
    }

    [DataTestMethod]
    [DataRow(0, true, true)]
    [DataRow(1, false, false)]
    [DataRow(2, true, false)]
    public void Load_LegacyDisplayModes_PreservesExistingDisplay(int mode, bool doubleLine, bool translation)
    {
        File.WriteAllText(SettingsPath, "{\"desktopLyrics\":{\"displayMode\":" + mode + "}}");
        var options = new SettingsStore(SettingsPath).Load().DesktopLyrics;
        Assert.AreEqual(doubleLine, options.ShowDoubleLine);
        Assert.AreEqual(translation, options.ShowTranslation);
    }

    [TestMethod]
    public void TrySave_DesktopSettings_RoundTripsPositionAndLock()
    {
        var options = new DesktopLyricsOptions(Enabled: true, Locked: true, FontSize: 48,
            TextColor: "#00FFAA", Position: new("monitor", 0.2, 0.1, 700, 240), KaraokeEnabled: true, HighlightColor: "#FFCC00", DoubleLineEnabled: false, TranslationEnabled: true,
            Layout: DesktopLyricsLayout.Horizontal);
        var store = new SettingsStore(SettingsPath);
        Assert.IsTrue(store.TrySave(new AppSettings { DesktopLyrics = options }));
        Assert.AreEqual(options, store.Load().DesktopLyrics);
        Assert.IsFalse(File.ReadAllText(SettingsPath).Contains("includeLyricsTranslation"));
    }

    [TestMethod]
    public void TrySave_LockedFile_ReportsFailureAndKeepsStoredSettings()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.IsTrue(store.TrySave(new AppSettings()));
        using (var locked = File.Open(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsFalse(store.TrySave(new AppSettings { DesktopLyrics = new(Enabled: true) }));
        }
        Assert.IsFalse(store.Load().DesktopLyrics.Enabled);
    }
}
