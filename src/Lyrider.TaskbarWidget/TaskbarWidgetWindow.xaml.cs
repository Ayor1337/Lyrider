using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace Lyrider.TaskbarWidget;

public partial class TaskbarWidgetWindow : Window
{
    private const double LogicalWidth = 216;
    private const double LogicalHeight = 40;
    private const double MarqueeSpeed = 30;
    private const double LyricTransitionOffset = 12;
    private static readonly TimeSpan LyricTransitionDuration = TimeSpan.FromMilliseconds(220);
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
    private bool _isPointerOver;
    private bool _isRefreshingHost;
    private string _primaryText = string.Empty;
    private int _lyricTransitionGeneration;
    private Color _idleBackgroundColor = Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
    private Color _hoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
    private readonly SolidColorBrush _rootBackgroundBrush = new();

    public TaskbarWidgetWindow()
    {
        InitializeComponent();
        Opacity = 0;
        RootBorder.Background = _rootBackgroundBrush;
        ApplySystemTheme();
        SourceInitialized += TaskbarWidgetWindow_SourceInitialized;
    }

    public event Action<TaskbarPlaybackCommand>? CommandRequested;

    public nint Handle => new WindowInteropHelper(this).Handle;

    public void SetPlaybackState(TaskbarPlaybackState state)
    {
        var previousState = _state;
        _state = state;
        if (!TaskbarPresentation.ShouldShow(state))
        {
            HideWidget();
            return;
        }

        var displayText = TaskbarPresentation.GetDisplayText(state);
        var primaryTextChanged = !string.Equals(_primaryText, displayText.Primary, StringComparison.Ordinal);
        var lyricTransitionDirection = TaskbarPresentation.GetLyricTransitionDirection(previousState, state);
        var previousDisplayText = TaskbarPresentation.GetDisplayText(previousState);
        _primaryText = displayText.Primary;
        if (primaryTextChanged)
        {
            StopMarquee();
        }
        TitleText.Text = displayText.Primary;
        ArtistText.Text = displayText.Secondary;
        PlayPauseIcon.Text = TaskbarPresentation.GetPlayPauseGlyph(state.IsPlaying);
        SetArtwork(state.ArtworkUrl);
        UpdateVisibility();
        if (lyricTransitionDirection != 0 && !_isPointerOver)
        {
            AnimateLyricTransition(previousDisplayText, lyricTransitionDirection);
        }
        else if (primaryTextChanged && !_isPointerOver)
        {
            Dispatcher.BeginInvoke(new Action(StartMarquee));
        }
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
            HostCanvas.Width = taskbarWidth / scale;
            HostCanvas.Height = taskbarHeight / scale;
            HostCanvas.UpdateLayout();
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
                0,
                0);
            var region = NativeMethods.CreateRectRgn(
                hitRegion.Left - taskbarRect.Left,
                hitRegion.Top - taskbarRect.Top,
                hitRegion.Right - taskbarRect.Left,
                hitRegion.Bottom - taskbarRect.Top);
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
        _idleBackgroundColor = isLight
            ? Color.FromArgb(0x01, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _hoverBackgroundColor = isLight
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        Resources["PlaybackButtonHoverBrush"] = new SolidColorBrush(isLight
            ? Color.FromArgb(0x12, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        Resources["PlaybackButtonPressedBrush"] = new SolidColorBrush(isLight
            ? Color.FromArgb(0x20, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF));
        SetRootBackground(_isPointerOver ? _hoverBackgroundColor : _idleBackgroundColor, animate: false);
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

        _isPointerOver = true;
        StopLyricTransition();
        StopMarquee();
        ControlsPanel.IsHitTestVisible = true;
        AnimatePanel(InfoPanel, 0, -2, 100);
        AnimatePanel(ControlsPanel, 1, 0, 167);
        SetRootBackground(_hoverBackgroundColor, animate: true);
    }

    private void RootBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerOver = false;
        ControlsPanel.IsHitTestVisible = false;
        AnimatePanel(ControlsPanel, 0, 2, 100);
        AnimatePanel(InfoPanel, 1, 0, 167);
        SetRootBackground(_idleBackgroundColor, animate: true);
        Dispatcher.BeginInvoke(new Action(StartMarquee));
    }

    private void PrimaryTextViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isPointerOver)
        {
            Dispatcher.BeginInvoke(new Action(StartMarquee));
        }
    }

    private void StartMarquee()
    {
        StopMarquee();
        TitleText.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var distance = TaskbarPresentation.CalculateMarqueeDistance(
            TitleText.DesiredSize.Width,
            PrimaryTextViewport.ActualWidth);
        if (distance <= 0 || _isPointerOver)
        {
            return;
        }

        var travelSeconds = distance / MarqueeSpeed;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            -distance,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1 + travelSeconds))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            -distance,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 + travelSeconds))));

        ((TranslateTransform)TitleText.RenderTransform).BeginAnimation(
            TranslateTransform.XProperty,
            animation);
    }

    private void StopMarquee()
    {
        var transform = (TranslateTransform)TitleText.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private void AnimateLyricTransition(TaskbarDisplayText outgoingText, int direction)
    {
        StopLyricTransition();
        var generation = _lyricTransitionGeneration;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var outgoingTransform = (TranslateTransform)OutgoingInfoPanel.RenderTransform;
        var incomingTransform = (TranslateTransform)InfoPanel.RenderTransform;
        var incomingOffset = direction * LyricTransitionOffset;
        var outgoingOffset = -incomingOffset;

        OutgoingTitleText.Text = outgoingText.Primary;
        OutgoingArtistText.Text = outgoingText.Secondary;
        OutgoingInfoPanel.Visibility = Visibility.Visible;
        OutgoingInfoPanel.Opacity = 0;
        outgoingTransform.Y = outgoingOffset;
        InfoPanel.Opacity = 1;
        incomingTransform.Y = 0;

        OutgoingInfoPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, LyricTransitionDuration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        outgoingTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, outgoingOffset, LyricTransitionDuration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        InfoPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, LyricTransitionDuration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        var incomingAnimation = new DoubleAnimation(incomingOffset, 0, LyricTransitionDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        incomingAnimation.Completed += (_, _) =>
        {
            if (generation != _lyricTransitionGeneration)
            {
                return;
            }

            OutgoingInfoPanel.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(StartMarquee));
        };
        incomingTransform.BeginAnimation(TranslateTransform.YProperty, incomingAnimation);
    }

    private void StopLyricTransition()
    {
        _lyricTransitionGeneration++;
        OutgoingInfoPanel.BeginAnimation(OpacityProperty, null);
        InfoPanel.BeginAnimation(OpacityProperty, null);
        var outgoingTransform = (TranslateTransform)OutgoingInfoPanel.RenderTransform;
        var incomingTransform = (TranslateTransform)InfoPanel.RenderTransform;
        outgoingTransform.BeginAnimation(TranslateTransform.YProperty, null);
        incomingTransform.BeginAnimation(TranslateTransform.YProperty, null);
        outgoingTransform.Y = 0;
        incomingTransform.Y = 0;
        OutgoingInfoPanel.Opacity = 0;
        OutgoingInfoPanel.Visibility = Visibility.Collapsed;
        InfoPanel.Opacity = 1;
    }

    private static void AnimatePanel(UIElement element, double opacity, double offset, int durationMilliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var currentOpacity = element.Opacity;
        element.Opacity = opacity;
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentOpacity, opacity, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });

        if (element.RenderTransform is TranslateTransform transform)
        {
            var currentOffset = transform.X;
            transform.X = offset;
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(currentOffset, offset, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    private void SetRootBackground(Color color, bool animate)
    {
        var current = _rootBackgroundBrush.Color;
        _rootBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _rootBackgroundBrush.Color = color;
        if (!animate)
        {
            return;
        }

        _rootBackgroundBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(current, color, TimeSpan.FromMilliseconds(167))
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
