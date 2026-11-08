using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.TaskbarWidget.Tests;

[TestClass]
public sealed class TaskbarWidgetWindowTests
{
    [DataTestMethod]
    [DataRow(true, TaskbarAlignment.Left, "Short", "Next", false, 1)]
    [DataRow(true, TaskbarAlignment.Left, "Short", "Translation", true, 1)]
    [DataRow(true, TaskbarAlignment.Left, "Short", "Next", false, -1)]
    [DataRow(true, TaskbarAlignment.Left, "A long lyric that must overflow the viewport", "Next", false, 1)]
    [DataRow(true, TaskbarAlignment.Left, "Short", "A long translation that must overflow the viewport", true, 1)]
    [DataRow(false, TaskbarAlignment.Left, "Short", "Next", false, 1)]
    [DataRow(true, TaskbarAlignment.Center, "Short", "Next", false, 1)]
    public void SetPlaybackState_LyricChanges_AlignsBeforeTransitionCompletes(
        bool rightAlignEnabled, TaskbarAlignment alignment, string lyric, string secondary,
        bool translation, int index)
    {
        RunOnSta(() =>
        {
            var window = new TaskbarWidgetWindow();
            try
            {
                SetTaskbarAlignment(window, alignment);
                window.SetRightAlignedLyrics(rightAlignEnabled);
                window.SetPlaybackState(CreateState("Previous line", 0));
                Layout(window);
                DrainDispatcher();

                window.SetPlaybackState(CreateState(lyric, index) with
                {
                    SecondaryLyric = secondary,
                    SecondaryLyricIsTranslation = translation
                });
                var panel = Find<StackPanel>(window, "PrimaryMarqueePanel");
                var beforeLayout = ((TranslateTransform)panel.RenderTransform).X;
                Layout(window);
                var title = Find<TextBlock>(window, "TitleText");
                var viewport = Find<Grid>(window, "TextViewport");
                var contentWidth = translation
                    ? Math.Max(title.DesiredSize.Width, Find<TextBlock>(window, "ScrollingArtistText").DesiredSize.Width)
                    : title.DesiredSize.Width;
                // Overflowing text rests at zero; its right-aligned inset lives in the text padding.
                var expected = rightAlignEnabled && alignment == TaskbarAlignment.Left
                    ? Math.Max(0, viewport.ActualWidth - title.DesiredSize.Width)
                    : 0;
                if (contentWidth > viewport.ActualWidth)
                {
                    expected = 0;
                }
                var duringTransition = ((TranslateTransform)panel.RenderTransform).X;
                WaitForTransition();
                var afterTransition = ((TranslateTransform)panel.RenderTransform).X;

                Assert.AreEqual(Visibility.Collapsed, Find<StackPanel>(window, "OutgoingInfoPanel").Visibility);
                Assert.AreEqual(expected, afterTransition, 0.01);
                Assert.AreEqual(afterTransition, beforeLayout, 0.01,
                    "The incoming lyric must be aligned before the next layout pass.");
                Assert.AreEqual(afterTransition, duringTransition, 0.01,
                    "The incoming lyric must already be aligned during the transition.");
                if (translation)
                {
                    Assert.AreEqual(0,
                        ((TranslateTransform)Find<StackPanel>(window, "SecondaryMarqueePanel").RenderTransform).X, 0.01);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DataTestMethod]
    [DataRow("A long lyric that must overflow the viewport", "Short translation")]
    [DataRow("Short", "A long translation that must overflow the viewport")]
    public void SetPlaybackState_RightAlignedOverflow_ShorterLineStartsAtRightEdge(string lyric, string translation)
    {
        RunOnSta(() =>
        {
            var window = new TaskbarWidgetWindow();
            try
            {
                SetTaskbarAlignment(window, TaskbarAlignment.Left);
                window.SetRightAlignedLyrics(true);
                window.SetPlaybackState(CreateState(lyric, 0) with
                {
                    SecondaryLyric = translation,
                    SecondaryLyricIsTranslation = true
                });
                Layout(window);
                DrainDispatcher();

                var viewportWidth = Find<Grid>(window, "TextViewport").ActualWidth;
                AssertRestsOnRightEdge(window, "TitleText", "MarqueeTitleText", lyric, 14, viewportWidth);
                AssertRestsOnRightEdge(window, "ScrollingArtistText", "MarqueeArtistText", translation, 12, viewportWidth);
                Assert.AreEqual(0, ((TranslateTransform)Find<StackPanel>(window, "PrimaryMarqueePanel").RenderTransform).X, 0.01);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DataTestMethod]
    [DataRow("A long lyric that must overflow the viewport", "Short translation")]
    [DataRow("Short", "A long translation that must overflow the viewport")]
    public void SetIndependentLyricScrolling_RightAligned_OnlyOverflowingLineScrolls(string lyric, string translation)
    {
        RunOnSta(() =>
        {
            var window = new TaskbarWidgetWindow();
            try
            {
                SetTaskbarAlignment(window, TaskbarAlignment.Left);
                window.SetRightAlignedLyrics(true);
                window.SetIndependentLyricScrolling(true);
                window.SetPlaybackState(CreateState(lyric, 0) with
                {
                    SecondaryLyric = translation,
                    SecondaryLyricIsTranslation = true
                });
                Layout(window);
                DrainDispatcher();

                var viewportWidth = Find<Grid>(window, "TextViewport").ActualWidth;
                var title = Find<TextBlock>(window, "TitleText");
                var primary = (TranslateTransform)Find<StackPanel>(window, "PrimaryMarqueePanel").RenderTransform;
                var secondary = (TranslateTransform)Find<StackPanel>(window, "SecondaryMarqueePanel").RenderTransform;
                var lyricOverflows = MeasureText(lyric, 14) > viewportWidth;
                var translationOverflows = MeasureText(translation, 12) > viewportWidth;
                Assert.AreNotEqual(lyricOverflows, translationOverflows, "Each case needs exactly one long line.");

                Assert.AreEqual(lyricOverflows, primary.HasAnimatedProperties);
                Assert.AreEqual(translationOverflows, secondary.HasAnimatedProperties);
                Assert.AreEqual(0, title.Padding.Left, 0.01, "A scrolling line needs no right-aligned inset.");
                Assert.AreEqual(0, Find<TextBlock>(window, "ScrollingArtistText").Padding.Left, 0.01);
                if (!lyricOverflows)
                {
                    Assert.AreEqual(viewportWidth - title.DesiredSize.Width, primary.X, 0.01,
                        "A still lyric must rest against the right edge.");
                }

                Assert.AreEqual(
                    translationOverflows ? Visibility.Collapsed : Visibility.Visible,
                    Find<TextBlock>(window, "ArtistText").Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PointerHover_RunningMarquee_KeepsScrollProgress(bool rightAlignEnabled)
    {
        RunOnSta(() =>
        {
            var window = new TaskbarWidgetWindow();
            try
            {
                SetTaskbarAlignment(window, TaskbarAlignment.Left);
                window.SetRightAlignedLyrics(rightAlignEnabled);
                window.SetPlaybackState(CreateState("A long lyric that must overflow the viewport", 0));
                Layout(window);
                DrainDispatcher();
                var viewport = Find<Grid>(window, "PrimaryMarqueeViewport");
                var transform = (TranslateTransform)Find<StackPanel>(window, "PrimaryMarqueePanel").RenderTransform;
                var runningMask = viewport.OpacityMask;
                Assert.IsNotNull(runningMask, "The marquee must be running before hovering.");

                InvokeHandler(window, "RootBorder_MouseEnter");
                DrainDispatcher();
                InvokeHandler(window, "RootBorder_MouseLeave");
                DrainDispatcher();

                Assert.AreSame(runningMask, viewport.OpacityMask, "Hovering must not restart the marquee.");
                Assert.IsTrue(transform.HasAnimatedProperties);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertRestsOnRightEdge(
        TaskbarWidgetWindow window, string lineName, string copyName, string text, double fontSize, double viewportWidth)
    {
        var line = Find<TextBlock>(window, lineName);
        var copy = Find<TextBlock>(window, copyName);
        var textWidth = MeasureText(text, fontSize);
        var expectedInset = Math.Max(0, viewportWidth - textWidth);
        Assert.AreEqual(expectedInset, line.Padding.Left, 0.01, $"{lineName} must rest against the right edge.");
        Assert.AreEqual(line.Padding.Left, copy.Padding.Left, 0.01, $"{copyName} must loop with the same inset.");
    }

    private static double MeasureText(string text, double fontSize)
    {
        var block = new TextBlock { Text = text, FontSize = fontSize, TextWrapping = TextWrapping.NoWrap };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    private static void InvokeHandler(TaskbarWidgetWindow window, string method) =>
        typeof(TaskbarWidgetWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [Find<Border>(window, "RootBorder"), null]);

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SetRightAlignedLyrics_ExistingLyric_AlignsBeforeDeferredMarquee(bool enabled)
    {
        RunOnSta(() =>
        {
            var window = new TaskbarWidgetWindow();
            try
            {
                SetTaskbarAlignment(window, TaskbarAlignment.Left);
                window.SetPlaybackState(CreateState("Short", 0));
                Layout(window);
                window.SetRightAlignedLyrics(!enabled);
                Invoke(window, "StartMarquee");

                window.SetRightAlignedLyrics(enabled);
                var beforeLayout = ((TranslateTransform)Find<StackPanel>(window, "PrimaryMarqueePanel").RenderTransform).X;
                Layout(window);
                var expected = enabled
                    ? Find<Grid>(window, "TextViewport").ActualWidth - Find<TextBlock>(window, "TitleText").ActualWidth
                    : 0;
                Assert.AreEqual(expected, beforeLayout, 0.01);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static TaskbarPlaybackState CreateState(string lyric, int index) =>
        new("Song", "Artist", null, true, true, lyric, "Next", index);

    private static T Find<T>(TaskbarWidgetWindow window, string name) =>
        (T)window.FindName(name);

    private static void Layout(TaskbarWidgetWindow window)
    {
        var root = Find<Border>(window, "RootBorder");
        root.Measure(new Size(216, 40));
        root.Arrange(new Rect(0, 0, 216, 40));
        root.UpdateLayout();
    }

    private static void SetTaskbarAlignment(TaskbarWidgetWindow window, TaskbarAlignment alignment)
    {
        var hostType = typeof(TaskbarWidgetWindow).GetNestedType("HostContext", BindingFlags.NonPublic)!;
        var host = Activator.CreateInstance(hostType, (nint)0, new PixelRect(0, 0, 1920, 48), (uint)96, alignment);
        typeof(TaskbarWidgetWindow).GetField("_hostContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, host);
    }

    private static void Invoke(TaskbarWidgetWindow window, string method) =>
        typeof(TaskbarWidgetWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void WaitForTransition()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The WPF test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
