using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class SettingsLayoutTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void SettingsPage_SaveFeedbackAndAction_AreNotInsideConnectionCard()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        Assert.IsNull(FindNamedElement(document, "SettingsStatusInfoBar"));
        var saveButton = FindNamedElement(document, "SaveSettingsButton");
        Assert.IsNotNull(saveButton);
        Assert.AreEqual("SettingsHeaderGrid", saveButton.Parent?.Attribute(XamlNamespace + "Name")?.Value);
        Assert.AreEqual("2", saveButton.Attribute("Grid.Column")?.Value);
        var headerColumns = saveButton.Parent!
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .ToArray();
        Assert.AreEqual("50", headerColumns[^1].Attribute("Width")?.Value);
    }

    [TestMethod]
    public void StartupLoadingOverlay_UsesAnActiveProgressRing()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        var overlay = FindNamedElement(document, "StartupLoadingOverlay");
        Assert.IsNotNull(overlay);
        Assert.AreEqual("2", overlay.Attribute("Grid.RowSpan")?.Value);
        var content = FindNamedElement(document, "StartupLoadingContent");
        Assert.IsNotNull(content);
        Assert.AreEqual("28", content.Attribute("Spacing")?.Value);
        var icon = overlay.Descendants().Single(element => element.Name.LocalName == "Image");
        Assert.AreEqual("Assets/Lyrider.png", icon.Attribute("Source")?.Value);
        Assert.IsTrue(overlay
            .Descendants()
            .Any(element => string.Equals(
                element.Attribute("Text")?.Value,
                "正在把旋律写进此刻…",
                StringComparison.Ordinal)));
        var progressRing = overlay.Descendants().Single(element => element.Name.LocalName == "ProgressRing");
        Assert.AreEqual("True", progressRing.Attribute("IsActive")?.Value);
    }

    [TestMethod]
    public void SettingsPage_LanguageSelector_OffersSystemChineseAndEnglish()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        var selector = FindNamedElement(document, "LanguageComboBox");
        Assert.IsNotNull(selector);
        CollectionAssert.AreEqual(
            new[] { "System", "zh-CN", "en-US" },
            selector.Elements().Select(element => element.Attribute("Tag")?.Value).ToArray());
    }

    [TestMethod]
    public void SettingsPage_LyricsSourceSelector_OffersAllProvidersAndProtectedKeyField()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        var selector = FindNamedElement(document, "LyricsSourceComboBox");
        Assert.IsNotNull(selector);
        CollectionAssert.AreEqual(
            new[] { "Auto", "Cider", "Netease", "QqMusic", "Musixmatch", "Lrclib" },
            selector.Elements().Select(element => element.Attribute("Tag")?.Value).ToArray());
        Assert.AreEqual(
            "LyricsSourceComboBox_SelectionChanged",
            selector.Attribute("SelectionChanged")?.Value);
        Assert.AreEqual(
            "ToggleSwitch",
            FindNamedElement(document, "LyricsTranslationToggle")?.Name.LocalName);
        Assert.AreEqual(
            "PasswordBox",
            FindNamedElement(document, "MusixmatchApiKeyPasswordBox")?.Name.LocalName);
    }

    [TestMethod]
    public void OnboardingPage_GuidesTokenSetupAndProvidesExplicitActions()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        var page = FindNamedElement(document, "OnboardingPageGrid");
        Assert.IsNotNull(page);
        Assert.AreEqual("2", page.Attribute("Grid.RowSpan")?.Value);
        Assert.AreEqual("Collapsed", page.Attribute("Visibility")?.Value);
        Assert.IsTrue(page.Descendants().Any(element =>
            string.Equals(element.Attribute("Text")?.Value, "打开 Cider“设置”，进入“连接”。", StringComparison.Ordinal)));
        Assert.IsTrue(page.Descendants().Any(element =>
            string.Equals(
                element.Attribute("Text")?.Value,
                "在“外部应用”中打开“管理外部应用对 Cider 的访问”，然后创建并复制 Token。",
                StringComparison.Ordinal)));
        Assert.IsNotNull(FindNamedElement(document, "OnboardingApiBaseUrlTextBox"));
        Assert.AreEqual(
            "PasswordBox",
            FindNamedElement(document, "OnboardingTokenPasswordBox")?.Name.LocalName);
        Assert.AreEqual(
            "验证连接",
            FindNamedElement(document, "ValidateOnboardingButton")?.Attribute("Content")?.Value);
        Assert.AreEqual(
            "确认并进入",
            FindNamedElement(document, "ConfirmOnboardingButton")?.Attribute("Content")?.Value);
        Assert.AreEqual(
            "Collapsed",
            FindNamedElement(document, "ConfirmOnboardingButton")?.Attribute("Visibility")?.Value);
        Assert.AreEqual(
            "稍后设置",
            FindNamedElement(document, "SkipOnboardingButton")?.Attribute("Content")?.Value);
    }

    private static XElement? FindNamedElement(XDocument document, string name) =>
        document.Descendants().SingleOrDefault(element =>
            string.Equals(element.Attribute(XamlNamespace + "Name")?.Value, name, StringComparison.Ordinal));
}
