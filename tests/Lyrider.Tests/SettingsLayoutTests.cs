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

        var connectionDialog = FindNamedElement(document, "ConnectionDialog");
        Assert.IsNotNull(connectionDialog);
        Assert.AreEqual("ContentDialog", connectionDialog.Name.LocalName);
        Assert.IsTrue(FindNamedElement(document, "ApiBaseUrlTextBox")!.Ancestors().Contains(connectionDialog));
        Assert.IsTrue(FindNamedElement(document, "TokenPasswordBox")!.Ancestors().Contains(connectionDialog));
        Assert.IsFalse(connectionDialog.Ancestors().Contains(FindNamedElement(document, "SettingsPageGrid")));
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
    public void SettingsPage_LyricsSourceSelector_HidesMusixmatchAndProtectedKeyField()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        var selector = FindNamedElement(document, "LyricsSourceComboBox");
        Assert.IsNotNull(selector);
        CollectionAssert.AreEqual(
            new[] { "Auto", "Cider", "Netease", "QqMusic", "Lrclib" },
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
        Assert.AreEqual(
            "Collapsed",
            FindNamedElement(document, "MusixmatchSettingsRow")?.Parent?.Attribute("Visibility")?.Value);
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

    [TestMethod]
    public void SettingsPage_ExperimentalSection_OwnsRightAlignedLyricsCardAboveCiderConnection()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));

        var toggle = FindNamedElement(document, "TaskbarLyricsAlignmentToggle");
        Assert.AreEqual("ToggleSwitch", toggle?.Name.LocalName);
        var card = FindNamedElement(document, "TaskbarLyricsAlignmentSettingsRow")?.Parent;
        Assert.IsNotNull(card);
        Assert.AreEqual("Border", card!.Name.LocalName);

        // The card closes a section of its own instead of sharing the "歌词与播放" cards.
        var cards = card.Parent;
        Assert.IsNotNull(cards);
        Assert.AreSame(card, cards!.Elements().Last());
        var section = cards.Parent;
        Assert.IsNotNull(section);
        var heading = section!.Elements().First();
        Assert.AreEqual("TextBlock", heading.Name.LocalName);
        Assert.AreEqual("实验", heading.Attribute("Text")?.Value);
        Assert.AreEqual("Main_100", heading.Attribute(XamlNamespace + "Uid")?.Value);

        // That section sits between "歌词与播放" and the Cider connection.
        Assert.AreEqual(
            "歌词与播放",
            section.ElementsBeforeSelf().Last().Elements().First().Attribute("Text")?.Value);
        Assert.AreEqual(
            "Cider 连接",
            section.ElementsAfterSelf().First().Elements().First().Attribute("Text")?.Value);
    }

    private static XElement? FindNamedElement(XDocument document, string name) =>
        document.Descendants().SingleOrDefault(element =>
            string.Equals(element.Attribute(XamlNamespace + "Name")?.Value, name, StringComparison.Ordinal));
}
