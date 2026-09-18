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
        var result = RepeatPresentation.ForMode(mode);

        Assert.AreEqual(label, result.Label);
        Assert.AreEqual(showOneBadge, result.ShowOneBadge);
        Assert.AreEqual(iconOpacity, result.IconOpacity);
    }
}
