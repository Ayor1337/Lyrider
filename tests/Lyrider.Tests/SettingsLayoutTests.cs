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

    private static XElement? FindNamedElement(XDocument document, string name) =>
        document.Descendants().SingleOrDefault(element =>
            string.Equals(element.Attribute(XamlNamespace + "Name")?.Value, name, StringComparison.Ordinal));
}
