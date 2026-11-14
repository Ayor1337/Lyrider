using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.TaskbarWidget.Tests;

[TestClass]
public sealed class DesktopLyricsRenderingTests
{
    [TestMethod]
    public void SetPlaybackState_TimingAnchorOnly_UpdatesProgressWithoutRelayout()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, KaraokeEnabled: true));
                var timing = new DesktopLyricsTiming([new(0, 2, 0, 1)], 0.5, Stopwatch.GetTimestamp());
                window.SetPlaybackState(new("Title", true, false, "你好世界", NextLyric: "下一句", Timing: timing));
                var content = (DesktopLyricsContent)window.FindName("LyricsContent");
                var row = content.RowDefinitions[0];
                Assert.AreEqual(0.25, content.PrimaryText.KaraokeProgress, 1e-9);

                window.SetPlaybackState(new("Title", true, false, "你好世界", NextLyric: "下一句",
                    Timing: timing with { Words = [new(0, 2, 0, 1)], Position = 1, ObservedTimestamp = Stopwatch.GetTimestamp() }));
                Assert.AreSame(row, content.RowDefinitions[0], "仅时间锚点变化时不应重建布局。");
                Assert.AreEqual(0.5, content.PrimaryText.KaraokeProgress, 1e-9);

                window.SetPlaybackState(new("Title", true, false, "新的一句", NextLyric: "下一句", Timing: timing));
                Assert.AreNotSame(row, content.RowDefinitions[0]);
                Assert.AreEqual("新的一句", content.PrimaryText.Text);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void SetText_JapaneseAndChineseCulture_SelectDifferentHanGlyphs()
    {
        RunOnSta(() =>
        {
            // “直”“骨”“写”在中日字形上差异明显，渲染结果应随语言变化。
            var japanese = RenderPixels(text => text.SetText("直骨写", 48, Colors.White, TextAlignment.Left,
                CultureInfo.GetCultureInfo("ja-JP")));
            var chinese = RenderPixels(text => text.SetText("直骨写", 48, Colors.White, TextAlignment.Left,
                CultureInfo.GetCultureInfo("zh-CN")));
            var chineseAgain = RenderPixels(text => text.SetText("直骨写", 48, Colors.White, TextAlignment.Left,
                CultureInfo.GetCultureInfo("zh-CN")));
            CollectionAssert.AreEqual(chinese, chineseAgain);
            CollectionAssert.AreNotEqual(japanese, chinese);
        });
    }

    [TestMethod]
    public void SongCulture_JapaneseChineseAndOther_PickMatchingLanguage()
    {
        Assert.AreEqual("ja-JP", DesktopLyricsContent.SongCulture(new("Song", true, true, IsJapaneseSong: true)).Name);
        Assert.AreEqual("zh-CN", DesktopLyricsContent.SongCulture(new("Song", true, true, IsChineseSong: true)).Name);
        Assert.AreEqual(CultureInfo.CurrentUICulture, DesktopLyricsContent.SongCulture(new("Song", true, true)));
    }

    [TestMethod]
    public void ApplyStyle_CustomFontFamily_ChangesTextLayout()
    {
        RunOnSta(() =>
        {
            static double Width(string? family)
            {
                var text = new OutlinedLyricText();
                text.ApplyStyle(new(FontFamily: family));
                text.SetText("iiiiiiii", 32, Colors.White, TextAlignment.Left);
                text.Measure(new Size(960, double.PositiveInfinity));
                var formatted = (FormattedText)typeof(OutlinedLyricText)
                    .GetField("_formatted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(text)!;
                return formatted.WidthIncludingTrailingWhitespace;
            }
            Assert.IsTrue(Width("Courier New") > Width(null) * 1.5, "等宽字体中 i 应明显更宽。");
            Assert.AreEqual(Width(null), Width("Not An Installed Font"), 0.01, "缺失的字体应回退到默认字体。");
        });
    }

    [DataTestMethod]
    [DataRow("，", "︐")]
    [DataRow("。", "︒")]
    [DataRow("「", "﹁")]
    [DataRow("」", "﹂")]
    [DataRow("（", "︵")]
    [DataRow("…", "︙")]
    [DataRow("A", "A")]
    [DataRow("你", "你")]
    public void ToVerticalForm_CjkPunctuation_UsesVerticalPresentationForm(string input, string expected) =>
        Assert.AreEqual(expected, OutlinedLyricText.ToVerticalForm(input));

    [TestMethod]
    public void MeasureVertical_LongVowelMarkAndComma_RotateAndMoveToUpperRight()
    {
        RunOnSta(() =>
        {
            var vertical = new DesktopLyricsOptions(TextDirection: DesktopLyricsTextDirection.Vertical, StrokeThickness: 0);
            var mark = Geometry(vertical, "ー");
            Assert.IsTrue(mark.Height > mark.Width * 2, $"竖排长音符应为竖线，实际 {mark}。");

            var comma = Geometry(vertical, "，");
            var cell = 48 * 1.25;
            Assert.IsTrue(comma.Left + comma.Width / 2 > cell / 2, $"竖排逗号应位于字格右侧，实际 {comma}。");
            Assert.IsTrue(comma.Top + comma.Height / 2 < cell / 2 + 2, $"竖排逗号应位于字格上半部，实际 {comma}。");
        });
    }

    private static Rect Geometry(DesktopLyricsOptions options, string value)
    {
        var text = new OutlinedLyricText();
        text.ApplyStyle(options);
        text.SetText(value, 48, Colors.White, TextAlignment.Left, CultureInfo.GetCultureInfo("zh-CN"));
        text.Measure(new Size(double.PositiveInfinity, 400));
        var geometry = (Geometry)typeof(OutlinedLyricText)
            .GetField("_geometry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(text)!;
        return geometry.Bounds;
    }

    private static byte[] RenderPixels(Action<OutlinedLyricText> setup)
    {
        var text = new OutlinedLyricText();
        text.ApplyStyle(new(StrokeThickness: 0));
        setup(text);
        text.Measure(new Size(400, double.PositiveInfinity));
        text.Arrange(new Rect(0, 0, 400, text.DesiredSize.Height));
        var bitmap = new RenderTargetBitmap(400, (int)Math.Ceiling(text.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
