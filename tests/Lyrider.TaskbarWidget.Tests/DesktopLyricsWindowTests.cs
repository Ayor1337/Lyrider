using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lyrider.TaskbarWidget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.TaskbarWidget.Tests;

[TestClass]
public sealed class DesktopLyricsWindowTests
{
    [TestMethod]
    public void QuickMenu_RightClick_OpensVerticalMenuAndAppliesSelectedCommands()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, TranslationEnabled: false, BackgroundOpacity: 0.45);
                window.ApplyOptions(options);
                window.SetPlaybackState(new("Song", true, false, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                var surface = (FrameworkElement)window.FindName("Surface");
                var menu = surface.ContextMenu!;
                MenuItem[] Commands(ItemCollection items) => items.OfType<MenuItem>()
                    .SelectMany(item => item.Tag is DesktopLyricsCommand ? new[] { item } : Commands(item.Items)).ToArray();
                var commands = Commands(menu.Items);
                MenuItem Find(DesktopLyricsCommand command) => commands.Single(item => Equals(item.Tag, command));
                void OpenAtLyrics()
                {
                    surface.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount, System.Windows.Input.MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                    PumpDispatcher(TimeSpan.FromMilliseconds(80));
                    Assert.IsTrue(menu.IsOpen, "The right-click context menu did not open.");
                }
                OpenAtLyrics();
                Assert.IsTrue(Find(DesktopLyricsCommand.AlignCenter).IsChecked);
                Assert.IsTrue(Find(DesktopLyricsCommand.DoubleLine).IsChecked);
                Assert.IsTrue(Find(DesktopLyricsCommand.HorizontalText).IsChecked);
                Assert.IsFalse(commands.Any(item => Equals(item.Tag, DesktopLyricsCommand.OpenSettings)));
                Assert.IsTrue(commands.All(item => !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(item))));
                var topItems = menu.Items.OfType<MenuItem>().ToArray();
                for (var index = 1; index < topItems.Length; index++)
                    Assert.IsTrue(topItems[index].TranslatePoint(new System.Windows.Point(), menu).Y >
                        topItems[index - 1].TranslatePoint(new System.Windows.Point(), menu).Y);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),
                    (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-quick-menu.png"))) encoder.Save(output);
                var alignmentGroup = (MenuItem)menu.Items[3];
                alignmentGroup.IsSubmenuOpen = true;
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                var popup = (Popup)alignmentGroup.Template.FindName("PART_Popup", alignmentGroup);
                Assert.IsTrue(popup.IsOpen);
                var submenu = (FrameworkElement)popup.Child;
                var alignmentItems = alignmentGroup.Items.OfType<MenuItem>().ToArray();
                Assert.IsTrue(alignmentItems.All(item => item.IsVisible && item.ActualHeight >= 28));
                Assert.IsTrue(((FrameworkElement)alignmentItems[0].Template.FindName("Check", alignmentItems[0])).IsVisible);
                bitmap = new RenderTargetBitmap((int)Math.Ceiling(submenu.ActualWidth),
                    (int)Math.Ceiling(submenu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(submenu);
                encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-quick-alignment.png"))) encoder.Save(output);
                menu.IsOpen = false;
                var closeWait = Stopwatch.StartNew();
                while (popup.IsOpen && closeWait.Elapsed < TimeSpan.FromSeconds(1))
                    PumpDispatcher(TimeSpan.FromMilliseconds(20));
                Assert.IsFalse(menu.IsOpen);
                Assert.IsFalse(popup.IsOpen, "The submenu remained open after its parent closed.");
                window.CommandRequested += command => { options = options.ApplyCommand(command); window.ApplyOptions(options); };
                foreach (var command in new[] { DesktopLyricsCommand.IncreaseFontSize, DesktopLyricsCommand.AlignRight,
                    DesktopLyricsCommand.SingleLine, DesktopLyricsCommand.VerticalText, DesktopLyricsCommand.ToggleTranslation })
                {
                    Find(command).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.IsFalse(menu.IsOpen);
                    OpenAtLyrics();
                }
                Assert.AreEqual(34, options.FontSize);
                Assert.IsTrue(Find(DesktopLyricsCommand.AlignRight).IsChecked);
                Assert.IsTrue(Find(DesktopLyricsCommand.SingleLine).IsChecked);
                Assert.IsTrue(Find(DesktopLyricsCommand.VerticalText).IsChecked);
                Assert.IsTrue(Find(DesktopLyricsCommand.ToggleTranslation).IsChecked);
                var current = (OutlinedLyricText)window.FindName("PrimaryText");
                Assert.AreEqual(34.0, (double)typeof(OutlinedLyricText).GetField("_fontSize", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)window.FindName("SecondaryText")).Visibility);
                window.ApplyOptions(options with { Locked = true });
                Assert.IsFalse(menu.IsOpen);
                Assert.IsNull(surface.ContextMenu);
                window.ApplyOptions(options with { Enabled = false });
                Assert.IsFalse(menu.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void QuickMenu_FontLimitsAndUnpersistedCommand_ReflectsAppliedOptionsOnly()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, FontSize: 72, Layout: DesktopLyricsLayout.Horizontal));
                var menu = ((FrameworkElement)window.FindName("Surface")).ContextMenu!;
                var increase = (MenuItem)menu.Items[0];
                var decrease = (MenuItem)menu.Items[1];
                Assert.IsFalse(increase.IsEnabled);
                Assert.IsTrue(decrease.IsEnabled);
                var alignment = (MenuItem)menu.Items[3];
                Assert.IsTrue(((MenuItem)alignment.Items[1]).IsChecked);
                DesktopLyricsCommand? requested = null;
                window.CommandRequested += command => requested = command;
                ((MenuItem)alignment.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.AreEqual(DesktopLyricsCommand.AlignLeft, requested);
                Assert.IsTrue(((MenuItem)alignment.Items[1]).IsChecked);
                Assert.IsFalse(((MenuItem)alignment.Items[2]).IsChecked);
                window.ApplyOptions(new(Enabled: true, FontSize: 16));
                Assert.IsTrue(increase.IsEnabled);
                Assert.IsFalse(decrease.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void LockedHover_FirstVisibleFrame_HasFinalToolbarPlacement()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            var unlock = new DesktopLyricsUnlockWindow();
            var presentations = new List<(NativeRect Bounds, bool LayoutReady)>();
            void CapturePresentation()
            {
                if (!unlock.IsVisible || unlock.Opacity == 0) return;
                GetWindowRect(new WindowInteropHelper(unlock).Handle, out var rect);
                var button = (FrameworkElement)unlock.FindName("UnlockButton");
                presentations.Add((rect, button.IsMeasureValid && button.IsArrangeValid));
            }
            var opacity = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Window))!;
            EventHandler opacityChanged = (_, _) => CapturePresentation();
            opacity.AddValueChanged(unlock, opacityChanged);
            unlock.IsVisibleChanged += (_, _) => CapturePresentation();
            try
            {
                window.ApplyOptions(new(Enabled: true, Locked: true));
                window.SetPlaybackState(new("Song", true, false, "歌词"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                unlock.Owner = window;
                ReadField<DispatcherTimer>(window, "_lockedHoverTimer")!.Stop();
                typeof(DesktopLyricsWindow).GetMethod("CloseUnlockWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                typeof(DesktopLyricsWindow).GetField("_unlockWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, unlock);
                GetWindowRect(new WindowInteropHelper(window).Handle, out var lyricsRect);
                UpdateLockedHover(window, new((lyricsRect.Left + lyricsRect.Right) / 2, (lyricsRect.Top + lyricsRect.Bottom) / 2));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                GetWindowRect(new WindowInteropHelper(unlock).Handle, out var final);
                Assert.IsTrue(presentations.Count > 0);
                foreach (var presentation in presentations)
                {
                    Assert.IsTrue(presentation.LayoutReady, "The unlock button became visible before layout finished.");
                    Assert.AreEqual(final.Left, presentation.Bounds.Left, "The unlock button moved horizontally after appearing.");
                    Assert.AreEqual(final.Top, presentation.Bounds.Top, "The unlock button moved vertically after appearing.");
                    Assert.AreEqual(final.Right, presentation.Bounds.Right);
                    Assert.AreEqual(final.Bottom, presentation.Bounds.Bottom);
                }
            }
            finally
            {
                opacity.RemoveValueChanged(unlock, opacityChanged);
                window.Close();
                unlock.Close();
            }
        });
    }

    [DataTestMethod]
    [DataRow(DesktopLyricsTextDirection.Horizontal, false)]
    [DataRow(DesktopLyricsTextDirection.Horizontal, true)]
    [DataRow(DesktopLyricsTextDirection.Vertical, false)]
    [DataRow(DesktopLyricsTextDirection.Vertical, true)]
    public void SetPlaybackState_WrappedLineChange_CommitsFinalBoundsWithoutIntermediateMoves(DesktopLyricsTextDirection direction, bool manualHeight)
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            HwndSource? source = null;
            var moves = new List<NativeRect>();
            nint ObserveMove(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                if (message == 0x0047 && window.IsVisible && window.Opacity > 0)
                {
                    GetWindowRect(hwnd, out var bounds);
                    moves.Add(bounds);
                }
                return 0;
            }
            try
            {
                window.ApplyOptions(new(Enabled: true, TranslationEnabled: false, TextDirection: direction,
                    Position: new(GetPrimaryMonitorName(), 0.5, 0.2, 320, manualHeight ? 400 : null)));
                window.SetPlaybackState(new("Song", true, false, "Short", NextLyric: "Next"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                source!.AddHook(ObserveMove);
                window.SetPlaybackState(new("Song", true, false,
                    "A much longer lyric that must wrap over two lines and change the window height", NextLyric: "Next"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                GetWindowRect(new WindowInteropHelper(window).Handle, out var final);
                foreach (var move in moves)
                {
                    Assert.AreEqual(final.Top, move.Top, "A visible intermediate move changed the vertical anchor.");
                    Assert.AreEqual(final.Bottom, move.Bottom, "A visible intermediate resize changed the bottom anchor.");
                }
            }
            finally
            {
                source?.RemoveHook(ObserveMove);
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SetPlaybackState_PendingFirstFrame_ShowsLatestContentAndCancelsWhenDisabled()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            var presented = new List<string>();
            var opacity = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Window))!;
            EventHandler opacityChanged = (_, _) =>
            {
                if (window.IsVisible && window.Opacity > 0)
                    presented.Add(((OutlinedLyricText)window.FindName("PrimaryText")).Text);
            };
            opacity.AddValueChanged(window, opacityChanged);
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, HideWhenPaused: true);
                window.ApplyOptions(options);
                window.SetPlaybackState(new("Song", true, true, "Old"));
                window.SetPlaybackState(new("Song", true, true, "Latest"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                CollectionAssert.AreEqual(new[] { "Latest" }, presented);
                window.SetPlaybackState(new("Song", true, false, "Latest"));
                window.SetPlaybackState(new("Song", true, true, "Cancelled"));
                window.ApplyOptions(options with { Enabled = false });
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                Assert.IsFalse(window.IsVisible);
                Assert.AreEqual(0, window.Opacity);
                CollectionAssert.AreEqual(new[] { "Latest" }, presented);
            }
            finally
            {
                opacity.RemoveValueChanged(window, opacityChanged);
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SetPlaybackState_FirstVisibleFrame_HasFinalLayoutAndPlacement()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            var presentations = new List<(NativeRect Bounds, bool LayoutReady)>();
            void CapturePresentation()
            {
                if (!window.IsVisible || window.Opacity == 0) return;
                var surface = (FrameworkElement)window.FindName("Surface");
                GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
                presentations.Add((rect, surface.IsMeasureValid && surface.IsArrangeValid));
            }
            var opacity = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Window))!;
            EventHandler opacityChanged = (_, _) => CapturePresentation();
            opacity.AddValueChanged(window, opacityChanged);
            window.IsVisibleChanged += (_, _) => CapturePresentation();
            try
            {
                window.ApplyOptions(new(Enabled: true, HideWhenPaused: true, FontSize: 40,
                    Position: new(GetPrimaryMonitorName(), 0.2, 0.2, 320)));
                foreach (var lyric in new[] { "第一次显示的长歌词，需要换行才能显示完整", "恢复播放后显示的另一句歌词" })
                {
                    presentations.Clear();
                    window.SetPlaybackState(new("Song", true, true, lyric, "Translation that also needs to wrap"));
                    PumpDispatcher(TimeSpan.FromMilliseconds(80));
                    GetWindowRect(new WindowInteropHelper(window).Handle, out var final);
                    Assert.IsTrue(presentations.Count > 0, "The prepared frame was never shown.");
                    foreach (var presentation in presentations)
                    {
                        Assert.IsTrue(presentation.LayoutReady, "A visible frame still needed layout.");
                        Assert.AreEqual(final.Left, presentation.Bounds.Left, "The window moved horizontally after becoming visible.");
                        Assert.AreEqual(final.Top, presentation.Bounds.Top, "The window moved vertically after becoming visible.");
                        Assert.AreEqual(final.Right, presentation.Bounds.Right);
                        Assert.AreEqual(final.Bottom, presentation.Bounds.Bottom);
                    }
                    window.SetPlaybackState(new("Song", true, false, lyric));
                    Assert.IsFalse(window.IsVisible);
                }
            }
            finally
            {
                opacity.RemoveValueChanged(window, opacityChanged);
                window.Close();
            }
        });
    }

    [DataTestMethod]
    [DataRow(320, DesktopLyricsTextDirection.Horizontal)]
    [DataRow(320, DesktopLyricsTextDirection.Vertical)]
    [DataRow(960, DesktopLyricsTextDirection.Horizontal)]
    [DataRow(960, DesktopLyricsTextDirection.Vertical)]
    public void Toolbar_AllActions_AreCenteredAboveLyricsAndKeepSpaceWhenHidden(int width, DesktopLyricsTextDirection direction)
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, TextDirection: direction, BackgroundOpacity: 0.45,
                    TranslationEnabled: false, Position: new(GetPrimaryMonitorName(), 0.5, 0.1, width, 400)));
                window.SetPlaybackState(new("Song", true, true, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                var toolbar = (StackPanel)window.FindName("Toolbar");
                var capsule = (Border)window.FindName("ToolbarSurface");
                toolbar.Visibility = Visibility.Visible;
                window.UpdateLayout();
                var origin = capsule.TranslatePoint(new System.Windows.Point(), window);
                Assert.AreEqual(window.ActualWidth / 2, origin.X + capsule.ActualWidth / 2, 1);
                Assert.IsTrue(origin.X >= 12);
                var content = (FrameworkElement)window.FindName("LyricsContent");
                var lyricsTop = content.TranslatePoint(new System.Windows.Point(), window).Y;
                Assert.IsTrue(origin.Y + capsule.ActualHeight <= lyricsTop);
                Assert.AreEqual(Visibility.Visible, capsule.Visibility);
                var buttons = toolbar.Children.OfType<System.Windows.Controls.Button>().ToArray();
                Assert.AreEqual(5, buttons.Length);
                Assert.IsNull(window.FindName("SettingsButton"));
                var commands = new List<DesktopLyricsCommand>();
                window.CommandRequested += commands.Add;
                foreach (var button in buttons)
                {
                    Assert.IsFalse(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)));
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                CollectionAssert.AreEqual(new[] { DesktopLyricsCommand.Previous, DesktopLyricsCommand.TogglePlayPause,
                    DesktopLyricsCommand.Next, DesktopLyricsCommand.ToggleLocked,
                    DesktopLyricsCommand.ToggleEnabled }, commands);
                var surface = (FrameworkElement)window.FindName("Surface");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                    (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"desktop-toolbar-{width}-{direction}.png")))
                    encoder.Save(output);
                toolbar.Visibility = Visibility.Hidden;
                window.UpdateLayout();
                Assert.AreEqual(Visibility.Hidden, capsule.Visibility);
                Assert.AreEqual(lyricsTop, content.TranslatePoint(new System.Windows.Point(), window).Y, 1);
                window.ApplyOptions(new(Enabled: true, Locked: true));
                Assert.AreEqual(Visibility.Hidden, toolbar.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void SetPlaybackState_PlayingAndPaused_UpdatesToolbarIconAndAccessibleAction()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true));
                var button = (System.Windows.Controls.Button)window.FindName("PlayPauseButton");
                var icon = (TextBlock)window.FindName("PlayPauseIcon");
                window.SetPlaybackState(new("Song", true, true, "歌词"));
                Assert.AreEqual("\uE769", icon.Text);
                var pauseName = System.Windows.Automation.AutomationProperties.GetName(button);
                Assert.AreEqual(button.ToolTip, pauseName);
                window.SetPlaybackState(new("Song", true, false, "歌词"));
                Assert.AreEqual("\uE768", icon.Text);
                var playName = System.Windows.Automation.AutomationProperties.GetName(button);
                Assert.AreEqual(button.ToolTip, playName);
                Assert.AreNotEqual(pauseName, playName);
            }
            finally { window.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SetPlaybackState_AlternatingLyrics_KeepsSlotsAndMovesHighlightAndKaraoke(bool karaoke)
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, Locked: true, KaraokeEnabled: karaoke,
                    TranslationEnabled: false, TextColor: "#FFFFFF", HighlightColor: "#00FFFF",
                    Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 640, 240));
                var primary = (OutlinedLyricText)window.FindName("PrimaryText");
                var secondary = (OutlinedLyricText)window.FindName("SecondaryText");
                var timing = new DesktopLyricsTiming([new(0, 10, 0, 1)], 5, Stopwatch.GetTimestamp());
                foreach (var layout in new[] { DesktopLyricsLayout.Vertical, DesktopLyricsLayout.Horizontal })
                {
                    window.ApplyOptions(options with { Layout = layout });
                    window.SetPlaybackState(new("歌曲", true, false, "第一句", NextLyric: "第二句", Timing: timing));
                    window.UpdateLayout();
                    var secondY = secondary.TranslatePoint(new System.Windows.Point(), window).Y;
                    Assert.AreEqual(karaoke ? 0.5 : 0, primary.KaraokeProgress);
                    Assert.AreEqual(0, secondary.KaraokeProgress);
                    window.SetPlaybackState(new("歌曲", true, false, "第二句", NextLyric: "第三句", Timing: timing, LineOrdinal: 1));
                    window.UpdateLayout();
                    Assert.AreEqual("第三句", primary.Text);
                    Assert.AreEqual("第二句", secondary.Text);
                    Assert.AreEqual(secondY, secondary.TranslatePoint(new System.Windows.Point(), window).Y, 1);
                    Assert.AreEqual(0, primary.KaraokeProgress);
                    Assert.AreEqual(karaoke ? 0.5 : 0, secondary.KaraokeProgress);
                    Assert.AreEqual(Colors.White, ReadField<SolidColorBrush>(primary, "_foreground")!.Color);
                    Assert.AreEqual(karaoke ? Colors.White : Colors.Cyan, ReadField<SolidColorBrush>(secondary, "_foreground")!.Color);
                    window.ApplyOptions(options with { Layout = layout, FontSize = 40 });
                    Assert.AreEqual("第二句", secondary.Text);
                    var surface = (FrameworkElement)window.FindName("Surface");
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                        (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"desktop-alternating-{layout}-karaoke-{karaoke}.png")))
                        encoder.Save(output);
                    window.SetPlaybackState(new("歌曲", true, false, "第三句", NextLyric: "最后一句", Timing: timing, LineOrdinal: 2));
                    Assert.AreEqual("第三句", primary.Text);
                    Assert.AreEqual("最后一句", secondary.Text);
                    Assert.AreEqual(karaoke ? 0.5 : 0, primary.KaraokeProgress);
                    Assert.AreEqual(0, secondary.KaraokeProgress);
                    window.SetPlaybackState(new("歌曲", true, false, "最后一句", Timing: timing, LineOrdinal: 3));
                    window.UpdateLayout();
                    Assert.AreEqual("", primary.Text);
                    Assert.AreEqual("最后一句", secondary.Text);
                    Assert.IsTrue(primary.ActualHeight > 0);
                    window.ApplyOptions(options with { Layout = layout, DoubleLineEnabled = false });
                    Assert.AreEqual("最后一句", primary.Text);
                    Assert.AreEqual(Visibility.Collapsed, secondary.Visibility);
                    Assert.AreEqual(karaoke ? 0.5 : 0, primary.KaraokeProgress);
                    Assert.AreEqual(0, secondary.KaraokeProgress);
                    window.SetPlaybackState(DesktopLyricsState.Unavailable);
                    Assert.IsFalse(window.IsVisible);
                    Assert.AreEqual(0, primary.KaraokeProgress);
                    Assert.AreEqual(0, secondary.KaraokeProgress);
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ResizeHandles_ExtremeDeltas_ClampAtLimitsAndKeepLyricsVisible()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, FontSize: 72, Layout: DesktopLyricsLayout.Horizontal));
                window.SetPlaybackState(new("Song", true, false, "夜空中最亮的星，照亮我们继续前行的路。",
                    NextLyric: "让每一句歌词都清晰可见，陪我们继续唱下去。"));
                ResizeBy(window, DesktopLyricsResizeEdge.Bottom | DesktopLyricsResizeEdge.Right, -10000, -10000);
                Assert.AreEqual(window.MinWidth, window.ActualWidth, 2);
                Assert.AreEqual(window.MinHeight, window.ActualHeight, 2);
                var content = (FrameworkElement)window.FindName("LyricsContent");
                Assert.IsTrue(content.DesiredSize.Height + 52 <= window.ActualHeight + 2);
                var surface = (FrameworkElement)window.FindName("Surface");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                    (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-minimum-size.png"))) encoder.Save(output);
                ResizeBy(window, DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Right, 10000, -10000);
                Assert.AreEqual(window.MaxWidth, window.ActualWidth, 2);
                Assert.AreEqual(window.MaxHeight, window.ActualHeight, 2);
                Assert.IsTrue(window.ActualWidth <= DesktopLyricsPlacement.MaximumWidth);
                Assert.IsTrue(window.ActualHeight <= DesktopLyricsPlacement.MaximumHeight);
                GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
                var area = GetPrimaryWorkArea();
                Assert.IsTrue(rect.Left >= area.Left && rect.Right <= area.Right && rect.Top >= area.Top && rect.Bottom <= area.Bottom);
                window.ResetPosition();
                Assert.AreEqual(SizeToContent.Height, window.SizeToContent);
                Assert.IsTrue(window.ActualHeight < window.MaxHeight);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void LockedHover_UnlockIcon_IsClickableWhileLyricsStayTransparentAndClickThrough()
    {
        RunOnSta(() =>
        {
            var underneath = new Window { Width = 960, Height = 300, ShowActivated = false,
                ShowInTaskbar = false, Topmost = true, WindowStyle = WindowStyle.None, Background = Brushes.CornflowerBlue };
            var lyrics = new DesktopLyricsWindow();
            try
            {
                underneath.Show();
                var options = new DesktopLyricsOptions(Enabled: true, Locked: true, BackgroundOpacity: 0.8);
                lyrics.ApplyOptions(options);
                lyrics.SetPlaybackState(new("Title", true, false, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                var timer = ReadField<DispatcherTimer>(lyrics, "_lockedHoverTimer")!;
                Assert.IsTrue(timer.IsEnabled);
                timer.Stop();
                var handle = new WindowInteropHelper(lyrics).Handle;
                GetWindowRect(handle, out var rect);
                SetWindowPos(new WindowInteropHelper(underneath).Handle, new nint(-1), rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0010);
                var point = new PixelPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                UpdateLockedHover(lyrics, point);
                var unlock = ReadField<DesktopLyricsUnlockWindow>(lyrics, "_unlockWindow")!;
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                Assert.IsTrue(unlock.IsVisible);
                Assert.IsFalse(unlock.IsActive);
                Assert.AreEqual(0, ((SolidColorBrush)((Border)lyrics.FindName("Backdrop")).Background).Color.A);
                Assert.AreEqual(0, ((SolidColorBrush)((Grid)lyrics.FindName("Surface")).Background).Color.A);
                Assert.AreEqual(0, ((SolidColorBrush)unlock.Background).Color.A);
                Assert.AreEqual(Visibility.Hidden, ((StackPanel)lyrics.FindName("Toolbar")).Visibility);
                var unlockHandle = new WindowInteropHelper(unlock).Handle;
                Assert.AreEqual(0L, GetWindowLongPtr(unlockHandle, -20).ToInt64() & 0x20);
                Assert.AreNotEqual(0L, GetWindowLongPtr(unlockHandle, -20).ToInt64() & 0x08000000);
                GetWindowRect(unlockHandle, out var iconRect);
                var iconScale = GetDpiForWindow(handle) / 96.0;
                var lockButton = (Button)lyrics.FindName("LockButton");
                var lockButtonTop = lockButton.TranslatePoint(new System.Windows.Point(), lyrics).Y;
                Assert.AreEqual(lockButton.ActualWidth * iconScale, iconRect.Right - iconRect.Left, 1);
                Assert.AreEqual(lockButton.ActualHeight * iconScale, iconRect.Bottom - iconRect.Top, 1);
                Assert.AreEqual((rect.Left + rect.Right) / 2.0, (iconRect.Left + iconRect.Right) / 2.0, 1);
                Assert.AreEqual(rect.Top + Math.Round(lockButtonTop * iconScale), iconRect.Top, 1);
                var iconPoint = new NativePoint { X = (iconRect.Left + iconRect.Right) / 2, Y = (iconRect.Top + iconRect.Bottom) / 2 };
                var renderWait = Stopwatch.StartNew();
                while (WindowFromPoint(iconPoint) != unlockHandle && renderWait.Elapsed < TimeSpan.FromSeconds(1))
                    PumpDispatcher(TimeSpan.FromMilliseconds(20));
                Assert.AreEqual(unlockHandle, WindowFromPoint(iconPoint), "The prepared unlock frame never became clickable.");
                Assert.AreEqual(new WindowInteropHelper(underneath).Handle, WindowFromPoint(new() { X = point.X, Y = point.Y }));
                UpdateLockedHover(lyrics, new(iconPoint.X, iconPoint.Y));
                Assert.IsTrue(unlock.IsVisible);
                var surface = (FrameworkElement)lyrics.FindName("Surface");
                lyrics.UpdateLayout();
                unlock.UpdateLayout();
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(new VisualBrush(surface), null, new(0, 0, surface.ActualWidth, surface.ActualHeight));
                    var scale = GetDpiForWindow(handle) / 96.0;
                    drawing.DrawRectangle(new VisualBrush(unlock.Content as Visual), null,
                        new((iconRect.Left - rect.Left) / scale, (iconRect.Top - rect.Top) / scale, unlock.ActualWidth, unlock.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-locked-hover-unlock.png"))) encoder.Save(output);
                DesktopLyricsCommand? command = null;
                lyrics.CommandRequested += requested => command = requested;
                ((Button)unlock.FindName("UnlockButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(DesktopLyricsCommand.ToggleLocked, command);
                lyrics.ApplyOptions(options with { Locked = false });
                Assert.IsFalse(timer.IsEnabled);
                Assert.IsNull(ReadField<DesktopLyricsUnlockWindow>(lyrics, "_unlockWindow"));
                Assert.AreEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x20);
                PumpDispatcher(TimeSpan.FromMilliseconds(300));
                Assert.IsTrue(((SolidColorBrush)((Border)lyrics.FindName("Backdrop")).Background).Color.A > 0);
            }
            finally { lyrics.Close(); underneath.Close(); }
        });
    }

    [TestMethod]
    public void LockedHover_LeaveHideAndClose_HidesIconAndReleasesTimer()
    {
        RunOnSta(() =>
        {
            var lyrics = new DesktopLyricsWindow();
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, Locked: true);
                lyrics.ApplyOptions(options);
                lyrics.SetPlaybackState(new("Title", true, false, "Current"));
                var timer = ReadField<DispatcherTimer>(lyrics, "_lockedHoverTimer")!;
                timer.Stop();
                GetWindowRect(new WindowInteropHelper(lyrics).Handle, out var rect);
                var point = new PixelPoint(rect.Left + 20, rect.Top + 50);
                // 歌词窗口尚未显示首帧时请求的图标，要在歌词显示后补上淡入。
                UpdateLockedHover(lyrics, point);
                var unlock = ReadField<DesktopLyricsUnlockWindow>(lyrics, "_unlockWindow")!;
                Assert.IsTrue(unlock.IsVisible);
                PumpDispatcher(TimeSpan.FromMilliseconds(100));
                // 歌词显示时会按真实鼠标位置刷新一次，这里停掉计时器后重新按测试位置悬停。
                timer.Stop();
                UpdateLockedHover(lyrics, point);
                PumpDispatcher(TimeSpan.FromMilliseconds(250));
                Assert.AreEqual(1, unlock.Opacity, 0.001, "解锁图标应淡入到完全不透明。");
                UpdateLockedHover(lyrics, new(rect.Left - 10, rect.Top));
                Assert.IsTrue(unlock.IsVisible, "鼠标移开后解锁图标先淡出。");
                Assert.IsTrue(unlock.HasAnimatedProperties);
                // 淡出途中移回来，从当前不透明度接着淡入，不会先隐藏。
                PumpDispatcher(TimeSpan.FromMilliseconds(60));
                UpdateLockedHover(lyrics, point);
                Assert.IsTrue(unlock.IsVisible);
                PumpDispatcher(TimeSpan.FromMilliseconds(250));
                Assert.AreEqual(1, unlock.Opacity, 0.001);
                UpdateLockedHover(lyrics, new(rect.Left - 10, rect.Top));
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                Assert.IsFalse(unlock.IsVisible, "淡出结束后隐藏。");
                UpdateLockedHover(lyrics, point);
                Assert.IsTrue(unlock.IsVisible);
                lyrics.SetPlaybackState(DesktopLyricsState.Unavailable);
                Assert.IsFalse(timer.IsEnabled);
                Assert.IsFalse(unlock.IsVisible);
                lyrics.SetPlaybackState(new("Title", true, false, "Current"));
                Assert.IsTrue(timer.IsEnabled);
                timer.Stop();
                UpdateLockedHover(lyrics, point);
                lyrics.ApplyOptions(options with { HideWhenPaused = true });
                Assert.IsFalse(unlock.IsVisible);
                Assert.IsFalse(timer.IsEnabled);
                lyrics.ApplyOptions(options);
                Assert.IsTrue(timer.IsEnabled);
                timer.Stop();
                UpdateLockedHover(lyrics, point);
                var unlockHandle = new WindowInteropHelper(unlock).Handle;
                lyrics.Close();
                Assert.IsFalse(timer.IsEnabled);
                Assert.IsFalse(IsWindow(unlockHandle));
            }
            finally { if (lyrics.IsVisible) lyrics.Close(); }
        });
    }

    private static void UpdateLockedHover(DesktopLyricsWindow lyrics, PixelPoint point) =>
        typeof(DesktopLyricsWindow).GetMethod("UpdateLockedHover", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lyrics, [point]);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SetPlaybackState_WithoutWordTiming_HighlightsCurrentAndUsesEqualFontSizes(bool karaoke)
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, Locked: true, KaraokeEnabled: karaoke,
                    TextColor: "#FFFFFF", HighlightColor: "#00FFFF", FontSize: 32,
                    Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 640, 240));
                foreach (var layout in new[] { DesktopLyricsLayout.Vertical, DesktopLyricsLayout.Horizontal })
                {
                    window.ApplyOptions(options with { Layout = layout });
                    window.SetPlaybackState(new("Song", true, false, "夜空中最亮的星", NextLyric: "夜空中最亮的星"));
                    window.UpdateLayout();
                    var primary = (OutlinedLyricText)window.FindName("PrimaryText");
                    var secondary = (OutlinedLyricText)window.FindName("SecondaryText");
                    Assert.AreEqual(primary.ActualHeight, secondary.ActualHeight, 1);
                    var surface = (FrameworkElement)window.FindName("Surface");
                    RenderTargetBitmap Render()
                    {
                        var result = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                            (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        result.Render(surface);
                        return result;
                    }
                    var bitmap = Render();
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    var secondTop = secondary.TranslatePoint(new System.Windows.Point(), window).Y;
                    var currentCyan = 0;
                    var nextCyan = 0;
                    for (var offset = 0; offset < pixels.Length; offset += 4)
                    {
                        if (pixels[offset] < 200 || pixels[offset + 1] < 200 || pixels[offset + 2] > 50) continue;
                        if ((offset / 4) / bitmap.PixelWidth < secondTop) currentCyan++;
                        else nextCyan++;
                    }
                    Assert.IsTrue(currentCyan > 100);
                    Assert.AreEqual(0, nextCyan);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"desktop-normal-highlight-{layout}-karaoke-{karaoke}.png")))
                        encoder.Save(output);
                    var previousHeight = primary.ActualHeight;
                    window.ApplyOptions(options with { Layout = layout, FontSize = 48 });
                    window.UpdateLayout();
                    Assert.AreEqual(primary.ActualHeight, secondary.ActualHeight, 1);
                    Assert.IsTrue(primary.ActualHeight > previousHeight);
                    window.SetPlaybackState(new("歌曲名称", true, false));
                    window.UpdateLayout();
                    var titleBitmap = Render();
                    var titlePixels = new byte[titleBitmap.PixelWidth * titleBitmap.PixelHeight * 4];
                    titleBitmap.CopyPixels(titlePixels, titleBitmap.PixelWidth * 4, 0);
                    var titleCyan = 0;
                    for (var offset = 0; offset < titlePixels.Length; offset += 4)
                        if (titlePixels[offset] > 200 && titlePixels[offset + 1] > 200 && titlePixels[offset + 2] < 50) titleCyan++;
                    Assert.AreEqual(0, titleCyan);
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_DirectionsAndAlignment_RespectTranslationAndSingleLineOverrides()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, Locked: true, TranslationEnabled: false,
                    Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 960, 400));
                var state = new DesktopLyricsState("Song", true, false, "Original ABC 123", "译文", "下一句");
                foreach (var direction in Enum.GetValues<DesktopLyricsTextDirection>())
                foreach (var alignment in Enum.GetValues<DesktopLyricsAlignment>())
                {
                    window.ApplyOptions(options with { TextDirection = direction, Alignment = alignment });
                    window.SetPlaybackState(state);
                    window.UpdateLayout();
                    var content = (DesktopLyricsContent)window.FindName("LyricsContent");
                    var first = content.PrimaryText.TranslatePoint(new System.Windows.Point(), window);
                    var second = content.SecondaryText.TranslatePoint(new System.Windows.Point(), window);
                    if (direction == DesktopLyricsTextDirection.Vertical)
                    {
                        Assert.AreEqual(first.Y, second.Y, 1);
                        Assert.IsTrue(second.X > first.X);
                        if (alignment == DesktopLyricsAlignment.Left) Assert.IsTrue(first.X < 40);
                        if (alignment == DesktopLyricsAlignment.Right) Assert.IsTrue(second.X > window.ActualWidth - 100);
                    }
                    else
                    {
                        Assert.IsTrue(second.Y > first.Y);
                    }
                    if (alignment == DesktopLyricsAlignment.Split)
                    {
                        window.ApplyOptions(options with { TextDirection = direction, Alignment = alignment, TranslationEnabled = true, DoubleLineEnabled = false });
                        window.UpdateLayout();
                        Assert.AreEqual("译文", content.SecondaryText.Text);
                        Assert.AreEqual(Visibility.Visible, content.SecondaryText.Visibility);
                        Assert.AreEqual(new Thickness(0), content.Margin);
                        window.ApplyOptions(options with { TextDirection = direction, Alignment = alignment, DoubleLineEnabled = false });
                        Assert.AreEqual(Visibility.Collapsed, content.SecondaryText.Visibility);
                        Assert.AreEqual(new Thickness(0), content.Margin);
                    }
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void CornerResize_RestoresBothDimensionsLocksAndResetsHeight()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            var restored = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true));
                window.SetPlaybackState(new("Title", true, false, "夜空中最亮的星", "The brightest star in the night sky"));
                DesktopLyricsPosition? saved = null;
                window.PositionChanged += position => saved = position;
                var initialWidth = window.ActualWidth;
                var initialHeight = window.ActualHeight;
                ResizeBy(window, DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Right, -120, -120);
                Assert.IsNotNull(saved);
                Assert.IsNotNull(saved.Height);
                Assert.AreEqual(initialWidth - 120, window.ActualWidth, 2);
                Assert.IsTrue(window.ActualHeight >= initialHeight + 115);
                var options = new DesktopLyricsOptions(Enabled: true, Position: saved);
                restored.ApplyOptions(options);
                restored.SetPlaybackState(new("Title", true, false, "夜空中最亮的星", "The brightest star in the night sky"));
                Assert.AreEqual(window.ActualHeight, restored.ActualHeight, 2);
                Assert.AreEqual(window.ActualWidth, restored.ActualWidth, 2);
                restored.ApplyOptions(options with { Locked = true });
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)restored.FindName("ResizeFrame")).Visibility);
                var lockedHeight = restored.ActualHeight;
                window.UpdateLayout();
                var surface = (FrameworkElement)window.FindName("Surface");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                    (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-lyrics-resized.png")))
                    encoder.Save(output);
                restored.ResetPosition();
                Assert.AreEqual(SizeToContent.Height, restored.SizeToContent);
                Assert.IsTrue(restored.ActualHeight < lockedHeight - 100);
            }
            finally { window.Close(); restored.Close(); }
        });
    }

    [TestMethod]
    public void TopEdgeResize_ChangesHeightAndKeepsItAfterLyricUpdates()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true));
                window.SetPlaybackState(new("Title", true, false, "Current lyric", "译文"));
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)window.FindName("ResizeFrame")).Visibility, "解锁后应提供缩放边框");
                var initialHeight = window.ActualHeight;
                var primary = (OutlinedLyricText)window.FindName("PrimaryText");
                var secondary = (OutlinedLyricText)window.FindName("SecondaryText");
                var initialGap = secondary.TranslatePoint(new System.Windows.Point(), window).Y
                    - primary.TranslatePoint(new System.Windows.Point(), window).Y - primary.ActualHeight;
                GetWindowRect(new WindowInteropHelper(window).Handle, out var before);
                ResizeBy(window, DesktopLyricsResizeEdge.Top, 0, -100, finish: false);
                Assert.IsTrue(window.ActualHeight >= initialHeight + 95);
                GetWindowRect(new WindowInteropHelper(window).Handle, out var during);
                Assert.AreEqual(before.Bottom, during.Bottom, "拖上边时底边应保持不动。");
                var enlargedGap = secondary.TranslatePoint(new System.Windows.Point(), window).Y
                    - primary.TranslatePoint(new System.Windows.Point(), window).Y - primary.ActualHeight;
                Assert.AreEqual(initialGap + 20, enlargedGap, 2);
                var height = window.ActualHeight;
                InvokePrivate(window, "EndResize");
                window.SetPlaybackState(new("Title", true, false, "New lyric"));
                window.UpdateLayout();
                Assert.AreEqual(height, window.ActualHeight, 2);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void SideEdgeResize_AutoHeight_StaysAutoAndKeepsBottom()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                DesktopLyricsPosition? saved = null;
                window.PositionChanged += position => saved = position;
                window.ApplyOptions(new(Enabled: true));
                window.SetPlaybackState(new("Title", true, false, "夜空中最亮的星，照亮我们继续前行的路，陪我们唱下去。",
                    NextLyric: "让每一句歌词都清晰可见，陪我们继续唱下去，直到天亮。"));
                var handle = new WindowInteropHelper(window).Handle;
                GetWindowRect(handle, out var before);
                var initialHeight = window.ActualHeight;
                var initialWidth = window.ActualWidth;
                ResizeBy(window, DesktopLyricsResizeEdge.Left, 500, 0);
                window.UpdateLayout();
                Assert.AreEqual(initialWidth - 500, window.ActualWidth, 2);
                Assert.AreEqual(initialWidth - 500, saved!.Width, 2);
                GetWindowRect(handle, out var after);
                Assert.AreEqual(SizeToContent.Height, window.SizeToContent, "只改宽度时应保持自动高度。");
                Assert.IsNull(saved?.Height);
                Assert.AreEqual(before.Right, after.Right, 1, "拖左边时右边应保持不动。");
                Assert.AreEqual(before.Bottom, after.Bottom, 1, "自动高度变化时底边应保持不动。");
                Assert.IsTrue(window.ActualHeight > initialHeight, "变窄后歌词换行，高度应随内容增加。");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void RestoreAutoHeight_AfterManualHeight_ReturnsToContentHeight()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                DesktopLyricsPosition? saved = null;
                window.PositionChanged += position => saved = position;
                window.ApplyOptions(new(Enabled: true));
                window.SetPlaybackState(new("Title", true, false, "Current lyric", "译文"));
                var initialHeight = window.ActualHeight;
                ResizeBy(window, DesktopLyricsResizeEdge.Top, 0, -150);
                Assert.AreEqual(SizeToContent.Manual, window.SizeToContent);
                Assert.IsNotNull(saved?.Height);
                var width = window.ActualWidth;
                InvokePrivate(window, "RestoreAutoHeight");
                window.UpdateLayout();
                Assert.AreEqual(SizeToContent.Height, window.SizeToContent);
                Assert.IsNull(saved?.Height);
                Assert.AreEqual(initialHeight, window.ActualHeight, 2);
                Assert.AreEqual(width, window.ActualWidth, 2);

                // 拖了下边但高度没变时，仍保持自动高度。
                ResizeBy(window, DesktopLyricsResizeEdge.Bottom, 0, 0);
                Assert.AreEqual(SizeToContent.Height, window.SizeToContent);
                Assert.IsNull(saved?.Height);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ResizeFrame_HoverAndResize_AlignsWithBackdropAndAnimatesMatchingIndicator()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, HighlightColor: "#FF8800"));
                window.SetPlaybackState(new("Title", true, false, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                window.UpdateLayout();
                var surface = (FrameworkElement)window.FindName("Surface");
                var backdrop = (Border)window.FindName("Backdrop");
                Assert.AreEqual(surface.ActualWidth, backdrop.ActualWidth, 0.5, "背景应铺满窗口宽度。");
                Assert.AreEqual(surface.ActualHeight, backdrop.ActualHeight, 0.5, "背景应铺满窗口高度。");
                Assert.AreEqual(new System.Windows.Point(), backdrop.TranslatePoint(new System.Windows.Point(), surface));

                string[] names = ["Left", "Right", "Top", "Bottom", "TopLeft", "TopRight", "BottomLeft", "BottomRight"];
                FrameworkElement Indicator(string name) => (FrameworkElement)window.FindName(name + "ResizeIndicator");
                string[] Shown() => names.Where(name => Indicator(name).Opacity > 0.99 &&
                    Indicator(name).RenderTransform is ScaleTransform { ScaleX: > 0.99, ScaleY: > 0.99 }).ToArray();
                string[] Visible() => names.Where(name => Indicator(name).Opacity > 0.01).ToArray();
                void Raise(string name, RoutedEvent routedEvent) =>
                    ((FrameworkElement)window.FindName(name + "ResizeEdge")).RaiseEvent(
                        new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });
                void Settle() => PumpDispatcher(TimeSpan.FromMilliseconds(450));

                CollectionAssert.AreEqual(Array.Empty<string>(), Visible());
                Raise("Left", Mouse.MouseEnterEvent);
                Assert.IsTrue(Indicator("Left").HasAnimatedProperties, "显示指示器应有过渡动画。");
                Settle();
                CollectionAssert.AreEqual(new[] { "Left" }, Shown());
                CollectionAssert.AreEqual(new[] { "Left" }, Visible(), "只显示所在边的药丸。");
                var pill = Indicator("Left");
                var center = pill.TranslatePoint(new System.Windows.Point(pill.ActualWidth / 2, pill.ActualHeight / 2), surface);
                Assert.AreEqual(surface.ActualHeight / 2, center.Y, 1, "药丸应位于边的中点。");

                // 从边移到相邻的角时，角先进入、边后离开也只保留角的指示。
                Raise("BottomLeft", Mouse.MouseEnterEvent);
                Raise("Left", Mouse.MouseLeaveEvent);
                Assert.IsTrue(Indicator("Left").HasAnimatedProperties, "隐藏指示器也应有过渡动画。");
                Settle();
                CollectionAssert.AreEqual(new[] { "BottomLeft" }, Shown());
                CollectionAssert.AreEqual(new[] { "BottomLeft" }, Visible());
                Raise("BottomLeft", Mouse.MouseLeaveEvent);
                Settle();
                CollectionAssert.AreEqual(Array.Empty<string>(), Visible());

                // 拖动期间即使鼠标不在边框上也保持指示，结束后淡出。
                InvokePrivate(window, "BeginResize", DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Right);
                Settle();
                CollectionAssert.AreEqual(new[] { "TopRight" }, Shown());
                InvokePrivate(window, "EndResize");
                Settle();
                CollectionAssert.AreEqual(Array.Empty<string>(), Visible());

                Raise("Top", Mouse.MouseEnterEvent);
                Settle();
                CollectionAssert.AreEqual(new[] { "Top" }, Shown());
                window.ApplyOptions(new(Enabled: true, Locked: true, HighlightColor: "#FF8800"));
                Settle();
                CollectionAssert.AreEqual(Array.Empty<string>(), Visible(), "锁定后不应残留缩放指示。");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void Backdrop_OpacityChangeAndLockToggle_FadeLikeIndicators()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var away = AwayFromCursor();
                window.ApplyOptions(new(Enabled: true, Position: away));
                window.SetPlaybackState(new("Title", true, false, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                PumpDispatcher(TimeSpan.FromMilliseconds(150));
                var brush = (SolidColorBrush)((Border)window.FindName("Backdrop")).Background;
                Assert.AreEqual(0, brush.Color.A);

                window.ApplyOptions(new(Enabled: true, BackgroundOpacity: 0.8, Position: away));
                Assert.IsTrue(brush.HasAnimatedProperties, "背景变化应有过渡动画。");
                Assert.IsTrue(brush.Color.A < 204, "过渡开始时不应立即到达目标。");
                PumpDispatcher(TimeSpan.FromMilliseconds(300));
                Assert.AreEqual(204, brush.Color.A);
                Assert.AreSame(brush, ((Border)window.FindName("Backdrop")).Background, "动画应作用在同一个画刷上。");

                // 内容刷新不应重新开始动画。
                window.SetPlaybackState(new("Title", true, false, "照亮我们前行的路"));
                Assert.AreEqual(204, brush.Color.A);

                window.ApplyOptions(new(Enabled: true, Locked: true, BackgroundOpacity: 0.8, Position: away));
                Assert.IsTrue(brush.HasAnimatedProperties, "锁定时背景也应渐变消失。");
                Assert.IsTrue(brush.Color.A > 0);
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                Assert.AreEqual(0, brush.Color.A, "锁定后背景最终透明。");

                window.ApplyOptions(new(Enabled: true, BackgroundOpacity: 0.8, Position: away));
                Assert.IsTrue(brush.Color.A < 204, "解锁时背景渐变出现。");
                PumpDispatcher(TimeSpan.FromMilliseconds(300));
                Assert.AreEqual(204, brush.Color.A);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void Toolbar_HoverChangesAndLock_FadeWithBackdrop()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var away = AwayFromCursor();
                window.ApplyOptions(new(Enabled: true, Position: away));
                window.SetPlaybackState(new("Title", true, false, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                PumpDispatcher(TimeSpan.FromMilliseconds(150));
                var toolbar = (StackPanel)window.FindName("Toolbar");
                var capsule = (Border)window.FindName("ToolbarSurface");
                var surface = (FrameworkElement)window.FindName("Surface");
                var backdrop = (SolidColorBrush)((Border)window.FindName("Backdrop")).Background;
                var menu = surface.ContextMenu!;
                Assert.AreEqual(Visibility.Hidden, toolbar.Visibility);

                // 快捷菜单打开时视为悬停。
                menu.PlacementTarget = surface;
                menu.IsOpen = true;
                Assert.AreEqual(Visibility.Visible, toolbar.Visibility);
                Assert.IsTrue(capsule.HasAnimatedProperties, "工具栏出现应有过渡动画。");
                Assert.IsTrue(capsule.Opacity < 1);
                Assert.IsTrue(backdrop.HasAnimatedProperties, "背景应与工具栏同时过渡。");
                PumpDispatcher(TimeSpan.FromMilliseconds(300));
                Assert.AreEqual(1, capsule.Opacity, 0.001);
                Assert.IsTrue(capsule.IsHitTestVisible);

                menu.IsOpen = false;
                // 测试环境里菜单的 Closed 事件不一定及时触发，直接按鼠标离开时的路径刷新悬停状态。
                InvokePrivate(window, "UpdateHover");
                Assert.IsTrue(capsule.HasAnimatedProperties, "工具栏消失应有过渡动画。");
                PumpDispatcher(TimeSpan.FromMilliseconds(150));
                Assert.AreEqual(Visibility.Visible, toolbar.Visibility, "淡出期间仍可见。");
                Assert.IsTrue(capsule.Opacity is > 0 and < 1, $"淡出进行中，实际不透明度 {capsule.Opacity}。");
                Assert.IsFalse(capsule.IsHitTestVisible, "淡出期间不应响应点击。");
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                Assert.AreEqual(Visibility.Hidden, toolbar.Visibility);
                Assert.AreEqual(1, capsule.Opacity, 0.001, "淡出结束后恢复静止状态。");

                menu.IsOpen = true;
                PumpDispatcher(TimeSpan.FromMilliseconds(300));
                Assert.AreEqual(Visibility.Visible, toolbar.Visibility);
                window.ApplyOptions(new(Enabled: true, Locked: true, Position: away));
                Assert.IsFalse(capsule.IsHitTestVisible, "锁定后工具栏立即不可点击。");
                Assert.IsTrue(capsule.HasAnimatedProperties, "锁定时工具栏也应淡出。");
                PumpDispatcher(TimeSpan.FromMilliseconds(350));
                Assert.AreEqual(Visibility.Hidden, toolbar.Visibility);
                Assert.AreEqual(1, capsule.Opacity, 0.001);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_IndependentDisplaySwitches_UpdatesSecondLineAndPreservesKaraoke()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var options = new DesktopLyricsOptions(Enabled: true, Locked: true, KaraokeEnabled: true,
                    DoubleLineEnabled: true, TranslationEnabled: true);
                window.ApplyOptions(options);
                window.SetPlaybackState(new("歌曲", true, false, "夜空中最亮的星", "The brightest star in the night sky",
                    "照亮我们继续前行的路", new([new(0, 10, 0, 1)], 5, Stopwatch.GetTimestamp())));
                var primary = (OutlinedLyricText)window.FindName("PrimaryText");
                var secondary = (OutlinedLyricText)window.FindName("SecondaryText");
                Assert.AreEqual("The brightest star in the night sky", secondary.Text);
                Assert.AreEqual(0, primary.KaraokeProgress);
                window.ApplyOptions(options with { TranslationEnabled = false });
                Assert.AreEqual("照亮我们继续前行的路", secondary.Text);
                Assert.AreEqual(Visibility.Visible, secondary.Visibility);
                window.ApplyOptions(options with { DoubleLineEnabled = false });
                Assert.AreEqual(Visibility.Visible, secondary.Visibility);
                Assert.AreEqual(0, primary.KaraokeProgress);
                window.ApplyOptions(options);
                Assert.AreEqual("The brightest star in the night sky", secondary.Text);
                Assert.AreEqual(Visibility.Visible, secondary.Visibility);
                window.UpdateLayout();
                var surface = (FrameworkElement)window.FindName("Surface");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                    (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-two-line-translation.png"));
                encoder.Save(output);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void SetPlaybackState_KaraokeTimer_AdvancesPausesSeeksAndStopsOnClose()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, KaraokeEnabled: true));
                window.SetPlaybackState(new("Title", true, false));
                var timing = new DesktopLyricsTiming([new(0, 2, 0, 1)], 0.5, Stopwatch.GetTimestamp());
                window.SetPlaybackState(new("Title", true, true, "你好世界", NextLyric: "下一句", Timing: timing));
                var text = (OutlinedLyricText)window.FindName("PrimaryText");
                bool Animating() => ReadBool(window, "_karaokeAnimating");
                Assert.IsTrue(Animating());
                PumpDispatcher(TimeSpan.FromMilliseconds(150));
                Assert.IsTrue(text.KaraokeProgress > 0.3);
                window.SetPlaybackState(new("Title", true, false, "你好世界", NextLyric: "下一句", Timing: timing with { Position = 1 }));
                Assert.AreEqual(0.5, text.KaraokeProgress);
                Assert.IsFalse(Animating());
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                Assert.AreEqual(0.5, text.KaraokeProgress);
                window.SetPlaybackState(new("Title", true, false, "你好世界", Timing: timing with { Position = 0.2 }));
                Assert.AreEqual(0.1, text.KaraokeProgress);
                window.SetPlaybackState(new("New song", true, true));
                Assert.AreEqual(0, text.KaraokeProgress);
                Assert.IsFalse(Animating());
                window.SetPlaybackState(new("Title", true, true, "你好世界", Timing: timing with { ObservedTimestamp = Stopwatch.GetTimestamp() }));
                Assert.IsTrue(Animating());
                window.ApplyOptions(new(Enabled: true));
                Assert.AreEqual(0, text.KaraokeProgress);
                Assert.IsFalse(Animating());
                window.ApplyOptions(new(Enabled: true, KaraokeEnabled: true));
                Assert.IsTrue(Animating());
                window.Close();
                Assert.IsFalse(Animating());
            }
            finally { if (window.IsVisible) window.Close(); }
        });
    }

    [TestMethod]
    public void SetPlaybackState_KaraokeWrappedLyrics_RendersPartialAndCompleteHighlight()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, Locked: true, KaraokeEnabled: true, TranslationEnabled: false, HighlightColor: "#00FFFF",
                    BackgroundOpacity: 0.4, Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 640)));
                var timing = new DesktopLyricsTiming([new(0, 10, 0, 1)], 0, Stopwatch.GetTimestamp());
                const string lyric = "夜空中最亮的星，照亮我们继续前行的路。让每一句歌词都跟着歌声变色。";
                var counts = new List<int>();
                foreach (var progress in new[] { 0.0, 0.65, 1.0 })
                {
                    window.SetPlaybackState(new("Song", true, false, lyric, "Translation stays white",
                        Timing: timing with { Position = progress * 10 }));
                    window.UpdateLayout();
                    var surface = (FrameworkElement)window.FindName("Surface");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    var count = 0;
                    for (var offset = 0; offset < pixels.Length; offset += 4)
                        if (pixels[offset] > 200 && pixels[offset + 1] > 200 && pixels[offset + 2] < 50) count++;
                    counts.Add(count);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"desktop-karaoke-{progress:0.00}.png"));
                    encoder.Save(output);
                    Assert.AreEqual(0, ((OutlinedLyricText)window.FindName("SecondaryText")).KaraokeProgress);
                }
                Assert.AreEqual(0, counts[0]);
                Assert.IsTrue(counts[1] > 100);
                Assert.IsTrue(counts[2] > counts[1]);
                window.SetPlaybackState(new("Song", true, false, string.Concat(Enumerable.Repeat(lyric + "👩‍🚀e\u0301", 4)),
                    Timing: timing with { Position = 8.5 }));
                window.UpdateLayout();
                var text = (OutlinedLyricText)window.FindName("PrimaryText");
                var clipped = new RenderTargetBitmap(640, 100, 96, 96, PixelFormats.Pbgra32);
                clipped.Render(text);
                Assert.IsTrue(text.ActualHeight <= 32 * 2.6 + 4);
            }
            finally { window.Close(); }
        });
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [TestMethod]
    public void SetPlaybackState_TranslationAndFallback_UpdatesAndHidesWithoutActivation()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true));
                window.SetPlaybackState(new("Title", true, true, "Original", "译文", "Next"));
                Assert.AreEqual("Original", ((OutlinedLyricText)window.FindName("PrimaryText")).Text);
                Assert.AreEqual("译文", ((OutlinedLyricText)window.FindName("SecondaryText")).Text);
                Assert.IsTrue(window.IsVisible);
                Assert.IsFalse(window.IsActive);
                window.SetPlaybackState(new("歌曲", true, false, "夜空中最亮的星", NextLyric: "照亮我们前行的路"));
                var secondary = (OutlinedLyricText)window.FindName("SecondaryText");
                Assert.AreEqual("照亮我们前行的路", secondary.Text);
                Assert.AreEqual(Visibility.Visible, secondary.Visibility);
                window.UpdateLayout();
                var surface = (FrameworkElement)window.FindName("Surface");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),
                    (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-chinese-two-line.png")))
                    encoder.Save(output);
                window.SetPlaybackState(new("歌曲", true, false, "照亮我们前行的路", NextLyric: "再一起唱下去"));
                Assert.AreEqual("再一起唱下去", secondary.Text);
                window.ApplyOptions(new(Enabled: true, DoubleLineEnabled: false));
                Assert.AreEqual(Visibility.Collapsed, secondary.Visibility);
                window.ApplyOptions(new(Enabled: true));
                Assert.AreEqual(Visibility.Visible, secondary.Visibility);
                window.SetPlaybackState(new("New title", true, true));
                Assert.AreEqual("New title", ((OutlinedLyricText)window.FindName("PrimaryText")).Text);
                Assert.AreEqual(Visibility.Collapsed, ((OutlinedLyricText)window.FindName("SecondaryText")).Visibility);
                window.SetPlaybackState(DesktopLyricsState.Unavailable);
                Assert.IsFalse(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_LockAndUnlock_TogglesNativeClickThroughAndToolbar()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, Locked: true));
                window.SetPlaybackState(new("Title", true, true, "Original"));
                var handle = new WindowInteropHelper(window).Handle;
                var style = GetWindowLongPtr(handle, -20).ToInt64();
                Assert.AreNotEqual(0L, style & 0x20); // WS_EX_TRANSPARENT
                Assert.AreNotEqual(0L, style & 0x80000); // WS_EX_LAYERED
                Assert.AreNotEqual(0L, style & 0x08000000); // WS_EX_NOACTIVATE
                Assert.AreNotEqual(0L, style & 0x80); // WS_EX_TOOLWINDOW
                Assert.AreEqual(0L, style & 0x40000); // WS_EX_APPWINDOW
                Assert.AreEqual(Visibility.Hidden, ((StackPanel)window.FindName("Toolbar")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)window.FindName("ResizeFrame")).Visibility);
                window.ApplyOptions(new(Enabled: true));
                Assert.AreEqual(0L, GetWindowLongPtr(handle, -20).ToInt64() & 0x20);
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)window.FindName("ResizeFrame")).Visibility);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_HideWhenPaused_RestoresOnResume()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, HideWhenPaused: true));
                window.SetPlaybackState(new("Title", true, false));
                Assert.IsFalse(window.IsVisible);
                window.SetPlaybackState(new("Title", true, true));
                Assert.IsTrue(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_LockedOverlay_HitTestsTheWindowUnderneath()
    {
        RunOnSta(() =>
        {
            var underneath = new Window { Width = 960, Height = 300, ShowActivated = false,
                ShowInTaskbar = false, Topmost = true, WindowStyle = WindowStyle.None, Background = Brushes.CornflowerBlue };
            var lyrics = new DesktopLyricsWindow();
            try
            {
                underneath.Show();
                lyrics.ApplyOptions(new(Enabled: true));
                lyrics.SetPlaybackState(new("Title", true, true, "Current lyric"));
                PumpDispatcher(TimeSpan.FromMilliseconds(80));
                var lyricHandle = new WindowInteropHelper(lyrics).Handle;
                var underneathHandle = new WindowInteropHelper(underneath).Handle;
                GetWindowRect(lyricHandle, out var rect);
                SetWindowPos(underneathHandle, new nint(-1), rect.Left, rect.Top, rect.Right - rect.Left,
                    rect.Bottom - rect.Top, 0x0010);
                SetWindowPos(lyricHandle, new nint(-1), 0, 0, 0, 0, 0x0013);
                var point = new NativePoint { X = (rect.Left + rect.Right) / 2, Y = rect.Top + 50 };
                Assert.AreEqual(lyricHandle, WindowFromPoint(point));
                lyrics.ApplyOptions(new(Enabled: true, Locked: true));
                Assert.AreEqual(underneathHandle, WindowFromPoint(point));
                lyrics.ApplyOptions(new(Enabled: true));
                Assert.AreEqual(lyricHandle, WindowFromPoint(point));
            }
            finally { lyrics.Close(); underneath.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_RemovedMonitorAndReset_KeepsWindowInPrimaryWorkArea()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, Position: new("removed-monitor", 1, 1, 3840)));
                window.SetPlaybackState(new("Title", true, true));
                var handle = new WindowInteropHelper(window).Handle;
                var area = GetPrimaryWorkArea();
                GetWindowRect(handle, out var rect);
                Assert.IsTrue(rect.Left >= area.Left && rect.Top >= area.Top && rect.Right <= area.Right && rect.Bottom <= area.Bottom);
                window.ResetPosition();
                GetWindowRect(handle, out rect);
                Assert.AreEqual((area.Left + area.Right) / 2, (rect.Left + rect.Right) / 2, 2);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DragCompleted_WidthAdjusted_ReportsPositionAndLogicalWidth()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                DesktopLyricsPosition? saved = null;
                window.PositionChanged += position => saved = position;
                window.ApplyOptions(new(Enabled: true, Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 640)));
                window.SetPlaybackState(new("Title", true, true));
                ResizeBy(window, DesktopLyricsResizeEdge.Right, 100, 0);
                Assert.IsNotNull(saved);
                Assert.AreEqual(GetPrimaryMonitorName(), saved.Monitor);
                Assert.AreEqual(740, saved.Width, 1);
                Assert.IsTrue(saved.CenterRatio is >= 0 and <= 1);
                Assert.IsTrue(saved.BottomRatio is >= 0 and <= 1);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ApplyOptions_RepeatedHostEnableDisable_DisposesDispatcherAndWindows()
    {
        var host = new DesktopLyricsHost();
        var failed = false;
        host.Failed += () => failed = true;
        Thread? thread = null;
        try
        {
            host.Update(new("Title", true, true, "Current", "译文"));
            host.ApplyOptions(new(Enabled: true));
            var dispatcher = WaitForDispatcher(host);
            thread = ReadField<Thread>(host, "_thread");
            for (var index = 0; index < 3; index++)
            {
                dispatcher.Invoke(() => Assert.IsTrue(ReadField<DesktopLyricsWindow>(host, "_window")!.IsVisible), DispatcherPriority.ApplicationIdle);
                host.ApplyOptions(new());
                dispatcher.Invoke(() => Assert.IsNull(ReadField<DesktopLyricsWindow>(host, "_window")), DispatcherPriority.ApplicationIdle);
                host.ApplyOptions(new(Enabled: true));
            }
        }
        finally { host.Dispose(); }
        Assert.IsFalse(failed);
        Assert.IsNotNull(thread);
        Assert.IsFalse(thread.IsAlive);
        host.Dispose();
        host.Update(DesktopLyricsState.Unavailable);
    }

    [TestMethod]
    public void SetPlaybackState_LongMultilingualLyrics_RendersWithinTwoLinesAndExportsPreview()
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                window.ApplyOptions(new(Enabled: true, Locked: true, BackgroundOpacity: 0.35,
                    Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 640)));
                window.SetPlaybackState(new("Song", true, true,
                    "夜空中最亮的星，照亮我们继续前行的路。A very long lyric line that wraps and eventually ends with an ellipsis.",
                    "The brightest star in the night sky lights our way forward. 让每一句歌词都清晰可见。"));
                window.UpdateLayout();
                var primary = (OutlinedLyricText)window.FindName("PrimaryText");
                Assert.IsTrue(primary.ActualHeight <= 32 * 2.6 + 4);
                var surface = (FrameworkElement)window.FindName("Surface");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-lyrics-preview.png"));
                encoder.Save(output);
                window.ApplyOptions(new(Enabled: true, BackgroundOpacity: 0.45,
                    Position: new(GetPrimaryMonitorName(), 0.5, 0.1, 640)));
                ((StackPanel)window.FindName("Toolbar")).Visibility = Visibility.Visible;
                window.UpdateLayout();
                bitmap.Clear();
                bitmap.Render(surface);
                var toolbarEncoder = new PngBitmapEncoder();
                toolbarEncoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var toolbarOutput = File.Create(Path.Combine(AppContext.BaseDirectory, "desktop-lyrics-toolbar.png"));
                toolbarEncoder.Save(toolbarOutput);
            }
            finally { window.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SetPlaybackState_Translation_ColorsBothLinesAsSungAndRestoresOrdinaryHighlight(bool karaoke, bool hasTiming)
    {
        RunOnSta(() =>
        {
            var window = new DesktopLyricsWindow();
            try
            {
                var state = new DesktopLyricsState("Song", true, true, "Original ABC", "译文", "Next",
                    hasTiming ? new([new(0, 10, 0, 1)], 5, Stopwatch.GetTimestamp()) : null);
                foreach (var direction in Enum.GetValues<DesktopLyricsTextDirection>())
                {
                    window.ApplyOptions(new(Enabled: true, Locked: true, KaraokeEnabled: karaoke,
                        TextColor: "#FF0000", HighlightColor: "#00FFFF", TextDirection: direction));
                    window.SetPlaybackState(state);
                    var primary = (OutlinedLyricText)window.FindName("PrimaryText");
                    var secondary = (OutlinedLyricText)window.FindName("SecondaryText");
                    Assert.AreEqual(Colors.Cyan, ReadField<SolidColorBrush>(primary, "_foreground")!.Color);
                    Assert.AreEqual(Colors.Cyan, ReadField<SolidColorBrush>(secondary, "_foreground")!.Color);
                    Assert.AreEqual(0, primary.KaraokeProgress);
                    Assert.AreEqual(0, secondary.KaraokeProgress);
                    Assert.IsFalse(ReadBool(window, "_karaokeAnimating"));
                    window.SetPlaybackState(state with { Translation = null });
                    Assert.AreEqual(karaoke && hasTiming ? Colors.Red : Colors.Cyan,
                        ReadField<SolidColorBrush>(primary, "_foreground")!.Color);
                    Assert.AreEqual(Colors.Red, ReadField<SolidColorBrush>(secondary, "_foreground")!.Color);
                    Assert.AreEqual(karaoke && hasTiming, ReadBool(window, "_karaokeAnimating"));
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public async Task RenderPreview_Translation_UsesSungColorForBothLines()
    {
        using var host = new DesktopLyricsHost();
        var state = new DesktopLyricsState("Song", true, false, "Original ABC", "译文", "Next",
            new([new(0, 10, 0, 1)], 5, 0));
        foreach (var direction in Enum.GetValues<DesktopLyricsTextDirection>())
        {
            var bytes = await host.RenderPreviewAsync(new(KaraokeEnabled: true, TextDirection: direction,
                TextColor: "#FF0000", HighlightColor: "#00FFFF"), state, 0.5);
            RunOnSta(() =>
            {
                using var stream = new MemoryStream(bytes);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var sung = 0;
                var unsung = 0;
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    if (pixels[offset] > 180 && pixels[offset + 1] > 180 && pixels[offset + 2] < 80) sung++;
                    if (pixels[offset + 2] > 180 && pixels[offset + 1] < 80 && pixels[offset] < 80) unsung++;
                }
                Assert.IsTrue(sung > 100);
                Assert.AreEqual(0, unsung);
            });
        }
    }

    [TestMethod]
    public async Task RenderPreview_DisabledDesktop_ProducesBothDirectionsWithoutOpeningWindow()
    {
        using var host = new DesktopLyricsHost();
        var state = new DesktopLyricsState("Song", true, false, "陪你 ABC 2026 😀", "与你同行", "下一句",
            new([new(0, 10, 0, 1)], 5, 0));
        foreach (var direction in Enum.GetValues<DesktopLyricsTextDirection>())
        {
            var options = new DesktopLyricsOptions(TextDirection: direction, Alignment: DesktopLyricsAlignment.Split,
                DoubleLineEnabled: false, KaraokeEnabled: true, StrokeColor: "#FF00FF", StrokeThickness: 2);
            var bytes = await host.RenderPreviewAsync(options, state, 0.5);
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
            Assert.IsTrue(bytes.Length > 1000);
            Assert.IsNull(ReadField<DesktopLyricsWindow>(host, "_window"));
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, $"desktop-shared-preview-{direction}.png"), bytes);
        }
    }

    [TestMethod]
    public void SetText_VerticalLatinAndCombinedCharacters_RotatesTrimsAndHighlightsInReadingOrder()
    {
        RunOnSta(() =>
        {
            var text = new OutlinedLyricText();
            var options = new DesktopLyricsOptions(TextDirection: DesktopLyricsTextDirection.Vertical,
                FontWeight: DesktopLyricsFontWeight.Bold, StrokeThickness: 0);
            text.ApplyStyle(options);
            text.SetText("ABC123", 32, Colors.White);
            text.Measure(new System.Windows.Size(100, 440));
            var geometry = ReadField<Geometry>(text, "_geometry")!;
            Assert.IsTrue(geometry.Bounds.Height > geometry.Bounds.Width * 2);
            text.SetText("你e\u0301😀好ABCDEFGHIJKLMNOPQRSTUVWXYZ再见", 32, Colors.White);
            text.Measure(new System.Windows.Size(100, 180));
            text.Arrange(new Rect(0, 0, text.DesiredSize.Width, text.DesiredSize.Height));
            Assert.IsTrue(text.DesiredSize.Height <= 180);
            var elements = ReadField<List<Rect>>(text, "_verticalElements")!;
            Assert.IsTrue(elements.Count >= 3 && elements.Count < 32);
            Assert.IsTrue(elements[1].Top >= elements[0].Bottom - 1);
            Assert.IsTrue(elements[2].Top >= elements[1].Bottom - 1);
            int HighlightedPixels(double progress)
            {
                text.SetKaraokeProgress(progress, Colors.Red);
                text.UpdateLayout();
                var bitmap = new RenderTargetBitmap(100, 180, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(text);
                var pixels = new byte[100 * 180 * 4];
                bitmap.CopyPixels(pixels, 400, 0);
                var count = 0;
                for (var offset = 0; offset < pixels.Length; offset += 4)
                    if (pixels[offset + 2] > 180 && pixels[offset + 1] < 80) count++;
                return count;
            }
            var partial = HighlightedPixels(0.02);
            var advanced = HighlightedPixels(0.1);
            Assert.IsTrue(partial > 0);
            Assert.IsTrue(advanced > partial);
            Assert.IsTrue(HighlightedPixels(1) >= advanced);
        });
    }

    [TestMethod]
    public void ApplyStyle_OutlineWidthAndColor_ChangesRenderedPixels()
    {
        RunOnSta(() =>
        {
            var text = new OutlinedLyricText();
            text.SetText("描边 ABC", 32, Colors.White);
            int OutlinePixels(double thickness)
            {
                text.ApplyStyle(new(StrokeThickness: thickness, StrokeColor: "#FF0000"));
                text.Measure(new System.Windows.Size(300, 100));
                text.Arrange(new Rect(0, 0, 300, 100));
                text.UpdateLayout();
                var bitmap = new RenderTargetBitmap(300, 100, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(text);
                var pixels = new byte[300 * 100 * 4];
                bitmap.CopyPixels(pixels, 1200, 0);
                var count = 0;
                for (var offset = 0; offset < pixels.Length; offset += 4)
                    if (pixels[offset + 2] > 180 && pixels[offset + 1] < 80) count++;
                return count;
            }
            Assert.AreEqual(0, OutlinePixels(0));
            Assert.IsTrue(OutlinePixels(2) > 100);
            Assert.IsTrue(OutlinePixels(8) > OutlinePixels(2));
        });
    }

    private static Dispatcher WaitForDispatcher(DesktopLyricsHost host)
    {
        Assert.IsTrue(SpinWait.SpinUntil(() => ReadField<Dispatcher>(host, "_dispatcher") is not null, TimeSpan.FromSeconds(5)));
        return ReadField<Dispatcher>(host, "_dispatcher")!;
    }

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPosition(out NativePoint point);

    /// <summary>把窗口放到与真实鼠标相反的半屏，避免测试结果受鼠标悬停影响。</summary>
    private static DesktopLyricsPosition AwayFromCursor()
    {
        var area = GetPrimaryWorkArea();
        var cursorInLowerHalf = GetCursorPosition(out var cursor) && cursor.Y > area.Top + (area.Bottom - area.Top) / 2;
        return new(GetPrimaryMonitorName(), 0.5, cursorInLowerHalf ? 0.8 : 0.05, 640);
    }

    private static void InvokePrivate(object owner, string name, params object[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);

    /// <summary>模拟系统缩放循环：按逻辑像素移动所拖的边，经 WM_SIZING 约束后应用到窗口。</summary>
    private static void ResizeBy(DesktopLyricsWindow window, DesktopLyricsResizeEdge edges, double dx, double dy, bool finish = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var scale = GetDpiForWindow(handle) / 96.0;
        InvokePrivate(window, "BeginResize", edges);
        GetWindowRect(handle, out var rect);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Left)) rect.Left += (int)Math.Round(dx * scale);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Right)) rect.Right += (int)Math.Round(dx * scale);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Top)) rect.Top += (int)Math.Round(dy * scale);
        if (edges.HasFlag(DesktopLyricsResizeEdge.Bottom)) rect.Bottom += (int)Math.Round(dy * scale);
        var direction = edges switch
        {
            DesktopLyricsResizeEdge.Left => 1,
            DesktopLyricsResizeEdge.Right => 2,
            DesktopLyricsResizeEdge.Top => 3,
            DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Left => 4,
            DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Right => 5,
            DesktopLyricsResizeEdge.Bottom => 6,
            DesktopLyricsResizeEdge.Bottom | DesktopLyricsResizeEdge.Left => 7,
            _ => 8
        };
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(rect, buffer, false);
            Assert.AreEqual(1, (int)SendMessage(handle, 0x0214, direction, buffer), "WM_SIZING 应由桌面歌词窗口处理。");
            rect = Marshal.PtrToStructure<NativeRect>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        SetWindowPos(handle, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0004 | 0x0010);
        window.UpdateLayout();
        if (finish) InvokePrivate(window, "EndResize");
    }

    private static bool ReadBool(object owner, string name) =>
        (bool)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static T? ReadField<T>(object owner, string name) where T : class =>
        (T?)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    private static string GetPrimaryMonitorName() => System.Windows.Forms.Screen.PrimaryScreen!.DeviceName;

    private static NativeRect GetPrimaryWorkArea()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        return new NativeRect { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                // 同步 InvokeShutdown 遇到仍在关闭的菜单弹窗会卡死；与 DesktopLyricsHost 一致，排在已排队的工作之后再关闭。
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                Dispatcher.Run();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The WPF test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
