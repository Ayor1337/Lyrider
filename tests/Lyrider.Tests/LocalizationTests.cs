using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class LocalizationTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void MainWindow_LocalizableProperties_ExistInBothLanguages()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml"));
        var chinese = LoadResourceNames("zh-CN");
        var english = LoadResourceNames("en-US");

        var expected = xaml.Descendants()
            .SelectMany(element =>
            {
                var uid = element.Attribute(XamlNamespace + "Uid")?.Value;
                return uid is null
                    ? []
                    : element.Attributes()
                        .Where(attribute => ContainsChinese(attribute.Value))
                        .Select(attribute => $"{uid}.{attribute.Name.LocalName}");
            })
            .ToHashSet(StringComparer.Ordinal);

        CollectionAssert.AreEquivalent(expected.ToArray(), chinese.ToArray());
        CollectionAssert.AreEquivalent(expected.ToArray(), english.ToArray());
    }

    [TestMethod]
    public void EnglishResources_DoNotContainChineseFallbackText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Strings", "en-US", "Resources.resw");
        var values = XDocument.Load(path).Root!.Elements("data").Select(data => data.Element("value")?.Value ?? string.Empty);

        Assert.IsFalse(values.Any(ContainsChinese));
    }

    private static HashSet<string> LoadResourceNames(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Strings", language, "Resources.resw");
        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(data => data.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool ContainsChinese(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');
}
