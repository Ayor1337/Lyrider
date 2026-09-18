using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lyrider.TaskbarWidget;

public partial class TaskbarWidgetWindow : Window
{
    private const double LogicalWidth = 216;
    private const double LogicalHeight = 40;
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const int WmGetObject = 0x003D;
    private const int WmShowWindow = 0x0018;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmNcCalcSize = 0x0083;
    private const int WmImeSetContext = 0x0281;
    private const int WmImeNotify = 0x0282;

    private TaskbarPlaybackState _state = TaskbarPlaybackState.Unavailable;
    private Task<(PixelRect? Frame, PixelRect? Widgets)>? _automationQuery;
    private string? _artworkUrl;
    private nint _taskbarHandle;
    private bool _isAttached;
    private bool _isRefreshingHost;

    public TaskbarWidgetWindow()
    {
        InitializeComponent();
        Opacity = 0;
        ApplySystemTheme();
        SourceInitialized += TaskbarWidgetWindow_SourceInitialized;
    }

    public event Action<TaskbarPlaybackCommand>? CommandRequested;

    public nint Handle => new WindowInteropHelper(this).Handle;

    public void SetPlaybackState(TaskbarPlaybackState state)
    {
        _state = state;
        if (!TaskbarPresentation.ShouldShow(state))
        {
            HideWidget();
            return;
        }

        TitleText.Text = state.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(state.Artist) ? "—" : state.Artist;
        PlayPauseIcon.Text = TaskbarPresentation.GetPlayPauseGlyph(state.IsPlaying);
        SetArtwork(state.ArtworkUrl);
        UpdateVisibility();
    }

    public async Task RefreshHostAsync(CancellationToken cancellationToken)
    {
        if (_isRefreshingHost)
        {
            return;
        }

        _isRefreshingHost = true;
        try
        {
            ApplySystemTheme();
            var taskbarHandle = NativeMethods.FindWindow(TaskbarClassName, null);
            if (taskbarHandle == nint.Zero ||
                !NativeMethods.GetWindowRect(taskbarHandle, out var nativeTaskbarRect))
            {
                DetachAndHide();
                return;
            }

            var taskbarRect = nativeTaskbarRect.ToPixelRect();
            if (taskbarRect.IsEmpty || taskbarRect.Height >= taskbarRect.Width)
            {
                DetachAndHide();
                return;
            }

            if (_taskbarHandle != taskbarHandle)
            {
                _taskbarHandle = taskbarHandle;
                _automationQuery = null;
            }

            var automationBounds = await GetAutomationBoundsAsync(taskbarHandle);
            cancellationToken.ThrowIfCancellationRequested();
            var frame = IsUsableFrame(automationBounds.Frame, taskbarRect)
                ? automationBounds.Frame!.Value
                : taskbarRect;
            var widgets = IsInsideFrame(automationBounds.Widgets, frame)
                ? automationBounds.Widgets
                : null;
            var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(taskbarHandle));
            var scale = dpi / 96d;
            var physicalWidth = Math.Max(1, (int)Math.Round(LogicalWidth * scale));
            var desiredHeight = (int)Math.Round(LogicalHeight * scale);
            var physicalHeight = Math.Max(1, Math.Min(desiredHeight, frame.Height - (int)Math.Round(4 * scale)));
            var placement = TaskbarPlacement.Calculate(
                frame,
                widgets,
                physicalWidth,
                physicalHeight,
                Math.Max(1, (int)Math.Round(2 * scale)),
                Math.Max(1, (int)Math.Round(12 * scale)));
            var handle = Handle;
            var taskbarWidth = taskbarRect.Width;
            var taskbarHeight = taskbarRect.Height;
            ApplyChildWindowStyles(handle);
            if (NativeMethods.GetParent(handle) != taskbarHandle)
            {
                NativeMethods.SetParent(handle, taskbarHandle);
                if (NativeMethods.GetParent(handle) != taskbarHandle)
                {
                    DetachAndHide();
                    return;
                }
            }
            var clientPoint = new NativePoint { X = placement.X, Y = placement.Y };
            if (!NativeMethods.ScreenToClient(taskbarHandle, ref clientPoint))
            {
                DetachAndHide();
                return;
            }

            Canvas.SetLeft(RootBorder, clientPoint.X / scale);
            Canvas.SetTop(RootBorder, clientPoint.Y / scale);
            if (!NativeMethods.SetWindowPos(
                    handle,
                    nint.Zero,
                    0,
                    0,
                    taskbarWidth,
                    taskbarHeight,
                    NativeMethods.SwpNoZOrder |
                    NativeMethods.SwpNoActivate |
                    NativeMethods.SwpAsyncWindowPos |
                    NativeMethods.SwpShowWindow))
            {
                DetachAndHide();
                return;
            }

            var hitRegion = TaskbarPlacement.CalculateHitRegion(
                taskbarRect,
                widgets,
                placement,
                physicalWidth,
                physicalHeight,
                Math.Max(1, (int)Math.Round(8 * scale)));
            var region = NativeMethods.CreateRectRgn(
                hitRegion.Left - taskbarRect.Left,
                hitRegion.Top - taskbarRect.Top,
                hitRegion.Right - taskbarRect.Left + 1,
                hitRegion.Bottom - taskbarRect.Top + 1);
            if (region == nint.Zero)
            {
                DetachAndHide();
                return;
            }

            if (NativeMethods.SetWindowRgn(handle, region, true) == 0)
            {
                NativeMethods.DeleteObject(region);
                DetachAndHide();
                return;
            }

            // The system owns the region after SetWindowRgn succeeds.
            _isAttached = true;
            UpdateVisibility();
        }
        catch (OperationCanceledException)
        {
            DetachAndHide();
        }
        catch (Exception)
        {
            DetachAndHide();
        }
        finally
        {
            _isRefreshingHost = false;
        }
    }

    private void TaskbarWidgetWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyChildWindowStyles(Handle);
        HwndSource.FromHwnd(Handle)?.AddHook(WindowProc);
    }

    private static nint WindowProc(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message is WmGetObject or
            WmShowWindow or
            WmWindowPosChanging or
            WmNcCalcSize or
            WmImeSetContext or
            WmImeNotify)
        {
            handled = true;
        }

        return nint.Zero;
    }

    private async Task<(PixelRect? Frame, PixelRect? Widgets)> GetAutomationBoundsAsync(nint taskbarHandle)
    {
        if (_automationQuery is null || _automationQuery.IsCompleted)
        {
            _automationQuery = Task.Run(() => QueryAutomationBounds(taskbarHandle));
        }

        try
        {
            return await _automationQuery.WaitAsync(TimeSpan.FromMilliseconds(700));
        }
        catch (TimeoutException)
        {
            return (null, null);
        }
        catch (Exception)
        {
            _automationQuery = null;
            return (null, null);
        }
    }

    private static (PixelRect? Frame, PixelRect? Widgets) QueryAutomationBounds(nint taskbarHandle)
    {
        var root = AutomationElement.FromHandle(taskbarHandle);
        return (
            FindAutomationRect(root, "TaskbarFrame"),
            FindAutomationRect(root, "WidgetsButton"));
    }

    private static PixelRect? FindAutomationRect(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (element is null)
        {
            return null;
        }

        var bounds = element.Current.BoundingRectangle;
        return new PixelRect(
            (int)Math.Round(bounds.Left),
            (int)Math.Round(bounds.Top),
            (int)Math.Round(bounds.Right),
            (int)Math.Round(bounds.Bottom));
    }

    private static bool IsUsableFrame(PixelRect? candidate, PixelRect taskbar) =>
        candidate is { IsEmpty: false } frame &&
        frame.Left >= taskbar.Left &&
        frame.Top >= taskbar.Top &&
        frame.Right <= taskbar.Right &&
        frame.Bottom <= taskbar.Bottom;

    private static bool IsInsideFrame(PixelRect? candidate, PixelRect frame) =>
        candidate is { IsEmpty: false } rectangle &&
        rectangle.Left >= frame.Left &&
        rectangle.Top >= frame.Top &&
        rectangle.Right <= frame.Right &&
        rectangle.Bottom <= frame.Bottom;

    private static void ApplyChildWindowStyles(nint handle)
    {
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlStyle).ToInt64();
        style = (style & ~NativeMethods.WsPopup) | NativeMethods.WsChild;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlStyle, new nint(style));

        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle = (extendedStyle & ~NativeMethods.WsExAppWindow) |
            NativeMethods.WsExToolWindow |
            NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(extendedStyle));
    }

    private void SetArtwork(string? url)
    {
        if (string.Equals(_artworkUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _artworkUrl = url;
        ArtworkBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var artworkUri))
        {
            return;
        }

        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = artworkUri;
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
        }
        catch (Exception)
        {
            return;
        }
        bitmap.DownloadCompleted += (_, _) =>
        {
            if (ArtworkBorder.Background is ImageBrush brush && ReferenceEquals(brush.ImageSource, bitmap))
            {
                ArtworkPlaceholder.Visibility = Visibility.Collapsed;
            }
        };
        bitmap.DownloadFailed += (_, _) =>
        {
            if (ArtworkBorder.Background is ImageBrush brush && ReferenceEquals(brush.ImageSource, bitmap))
            {
                ArtworkBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                ArtworkPlaceholder.Visibility = Visibility.Visible;
            }
        };
        ArtworkBorder.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        if (!bitmap.IsDownloading)
        {
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplySystemTheme()
    {
        var isLight = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            isLight = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            // Keep the dark taskbar defaults when the preference cannot be read.
        }

        Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x1C, 0x1C, 0x1C)
            : Colors.White);
    }

    private void UpdateVisibility()
    {
        if (!_isAttached || !TaskbarPresentation.ShouldShow(_state))
        {
            HideWidget();
            return;
        }

        Opacity = 1;
        NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
    }

    private void DetachAndHide()
    {
        _isAttached = false;
        HideWidget();
    }

    private void HideWidget()
    {
        Opacity = 0;
        if (Handle != nint.Zero)
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SwHide);
        }
    }

    private void RootBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_state.IsAvailable)
        {
            return;
        }

        ControlsPanel.IsHitTestVisible = true;
        AnimateOpacity(InfoPanel, 0);
        AnimateOpacity(ControlsPanel, 1);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    }

    private void RootBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ControlsPanel.IsHitTestVisible = false;
        AnimateOpacity(InfoPanel, 1);
        AnimateOpacity(ControlsPanel, 0);
        RootBorder.Background = Brushes.Transparent;
    }

    private static void AnimateOpacity(UIElement element, double target)
    {
        var current = element.Opacity;
        element.Opacity = target;
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(TaskbarPlaybackCommand.Previous);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(TaskbarPlaybackCommand.TogglePlayPause);

    private void NextButton_Click(object sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(TaskbarPlaybackCommand.Next);

}
