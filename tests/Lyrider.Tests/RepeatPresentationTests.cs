using System.Globalization;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class RepeatPresentationTests
{
    [TestMethod]
    [DataRow(0, "循环：关闭", false, 0.55)]
    [DataRow(1, "循环：单曲循环", true, 1.0)]
    [DataRow(2, "循环：列表循环", false, 1.0)]
    public void ForMode_KnownMode_ReturnsDistinctPresentation(
        int mode,
        string label,
        bool showOneBadge,
        double iconOpacity)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        RepeatDisplayState result;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            result = RepeatPresentation.ForMode(mode);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }

        Assert.AreEqual(label, result.Label);
        Assert.AreEqual(showOneBadge, result.ShowOneBadge);
        Assert.AreEqual(iconOpacity, result.IconOpacity);
    }

    [TestMethod]
    [DataRow(0, "Repeat: Off")]
    [DataRow(1, "Repeat: One")]
    [DataRow(2, "Repeat: All")]
    public void ForMode_EnglishCulture_ReturnsEnglishLabel(int mode, string label)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.AreEqual(label, RepeatPresentation.ForMode(mode).Label);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
