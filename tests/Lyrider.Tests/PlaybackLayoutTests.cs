using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class PlaybackLayoutTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void PlaybackProgress_IsInteractiveSliderWithValueHandler()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));
        var progress = document.Descendants().Single(element =>
            string.Equals(
                element.Attribute(XamlNamespace + "Name")?.Value,
                "PlaybackProgressSlider",
                StringComparison.Ordinal));

        Assert.AreEqual("Slider", progress.Name.LocalName);
        Assert.AreEqual(
            "PlaybackProgressSlider_ValueChanged",
            progress.Attribute("ValueChanged")?.Value);
        Assert.AreNotEqual("False", progress.Attribute("IsHitTestVisible")?.Value);
    }

    [TestMethod]
    public void PlayerAndSettings_DoNotExposeVolumeControls()
    {
        var document = XDocument.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MainWindow.xaml"));
        var namedElements = document.Descendants()
            .Select(element => element.Attribute(XamlNamespace + "Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsFalse(namedElements.Contains("VolumePanel"));
        Assert.IsFalse(namedElements.Contains("VolumeSlider"));
        Assert.IsFalse(namedElements.Contains("VolumeSettingsRow"));
        Assert.IsFalse(namedElements.Contains("ShowVolumeToggle"));
    }
}
