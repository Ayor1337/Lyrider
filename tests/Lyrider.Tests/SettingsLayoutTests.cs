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

    private static XElement? FindNamedElement(XDocument document, string name) =>
        document.Descendants().SingleOrDefault(element =>
            string.Equals(element.Attribute(XamlNamespace + "Name")?.Value, name, StringComparison.Ordinal));
}
