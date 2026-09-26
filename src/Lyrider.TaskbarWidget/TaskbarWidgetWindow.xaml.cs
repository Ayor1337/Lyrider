using System.Diagnostics;
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
    private const double RightAnchorGap = 24;
    private const double MarqueeSpeed = 30;
    private const double MarqueeGap = 24;
    private static readonly TimeSpan MarqueeStartDelay = TimeSpan.FromSeconds(1);
    private const double LyricTransitionOffset = 12;
    private static readonly TimeSpan LyricTransitionDuration = TimeSpan.FromMilliseconds(220);
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const string TaskbarAlignmentRegistryPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const int WmGetObject = 0x003D;
    private const int WmShowWindow = 0x0018;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmNcCalcSize = 0x0083;
    private const int WmImeSetContext = 0x0281;
    private const int WmImeNotify = 0x0282;

    private TaskbarPlaybackState _state = TaskbarPlaybackState.Unavailable;
    private Task<AutomationBounds>? _automationQuery;
    private readonly TaskbarPlacementAnimation _placementAnimation = new();
    private readonly TranslateTransform _placementTransform = new();
    private readonly Stopwatch _placementClock = Stopwatch.StartNew();
    private HostContext? _hostContext;
    private PixelRect? _automationFrame;
    private (int Width, int Height)? _widgetSize;
    private AppliedLayout? _appliedLayout;
    private AnimationLayout? _animationLayout;
    private bool _isAnimatingPlacement;
    private string? _artworkUrl;
    private bool _isAttached;
    private bool _isPointerOver;
    private bool _isRefreshingHost;
    private string _primaryText = string.Empty;
    private string _secondaryText = string.Empty;
    private int _lyricTransitionGeneration;
    private Color _idleBackgroundColor = Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
    private Color _hoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
    private readonly SolidColorBrush _rootBackgroundBrush = new();

    private readonly record struct AutomationBounds(
        bool Succeeded, PixelRect? Frame, PixelRect? Widgets, PixelRect? SystemTray);

    private readonly record struct HostContext(
        nint Handle, PixelRect Rect, uint Dpi, TaskbarAlignment Alignment);

    private readonly record struct AppliedLayout(
        PixelPoint Position, int Width, int Height, double Scale, PixelRect HitRegion);

    private readonly record struct AnimationLayout(
        HostContext Host, PixelRect? Widgets, int WidgetWidth, int WidgetHeight);

    public TaskbarWidgetWindow()
    {
        InitializeComponent();
        PreviousButton.ToolTip = WidgetText.Get("上一首", "Previous");
        PlayPauseButton.ToolTip = WidgetText.Get("播放/暂停", "Play/Pause");
        NextButton.ToolTip = WidgetText.Get("下一首", "Next");
        Opacity = 0;
        RootBorder.Background = _rootBackgroundBrush;
        RootBorder.RenderTransform = _placementTransform;
        ApplySystemTheme();
        SourceInitialized += TaskbarWidgetWindow_SourceInitialized;
        Closed += (_, _) => StopPlacementAnimation();
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
        var displayTextChanged =
            !string.Equals(_primaryText, displayText.Primary, StringComparison.Ordinal) ||
            !string.Equals(_secondaryText, displayText.Secondary, StringComparison.Ordinal);
        var marqueeModeChanged =
            TaskbarPresentation.ShouldSynchronizeMarquee(previousState) !=
            TaskbarPresentation.ShouldSynchronizeMarquee(state);
        var lyricTransitionDirection = TaskbarPresentation.GetLyricTransitionDirection(previousState, state);
        var previousDisplayText = TaskbarPresentation.GetDisplayText(previousState);
        _primaryText = displayText.Primary;
        _secondaryText = displayText.Secondary;
        if (displayTextChanged || marqueeModeChanged || lyricTransitionDirection != 0)
        {
            StopMarquee();
        }
        TitleText.Text = displayText.Primary;
        MarqueeTitleText.Text = displayText.Primary;
        ArtistText.Text = displayText.Secondary;
        ScrollingArtistText.Text = displayText.Secondary;
        MarqueeArtistText.Text = displayText.Secondary;
        PlayPauseIcon.Text = TaskbarPresentation.GetPlayPauseGlyph(state.IsPlaying);
        SetArtwork(state.ArtworkUrl);
        UpdateVisibility();
        if (lyricTransitionDirection != 0 && !_isPointerOver)
        {
            AnimateLyricTransition(previousDisplayText, lyricTransitionDirection);
        }
        else if ((displayTextChanged || marqueeModeChanged) && !_isPointerOver)
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
            cancellationToken.ThrowIfCancellationRequested();
            var context = GetHostContext();
            if (context is not HostContext host)
            {
                DetachAndHide();
                return;
            }

            if (_hostContext != host)
            {
                ResetPlacement();
                _hostContext = host;
            }

            var taskbarHandle = host.Handle;
            var taskbarRect = host.Rect;
            var automationBounds = await GetAutomationBoundsAsync(taskbarHandle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetHostContext() != host)
            {
                // Explorer, DPI or taskbar settings changed while UI Automation was running.
                DetachAndHide();
                return;
            }

            if (automationBounds.Succeeded)
            {
                _automationFrame = IsInsideFrame(automationBounds.Frame, taskbarRect)
                    ? automationBounds.Frame
                    : null;
            }
            // Keep dimensions stable when a right-side anchor query temporarily fails.
            var frame = automationBounds.Succeeded || host.Alignment == TaskbarAlignment.Left
                ? _automationFrame ?? taskbarRect
                : taskbarRect;
            var widgets = IsInsideFrame(automationBounds.Widgets, frame)
                ? automationBounds.Widgets
                : null;
            var nativeTray = GetSystemTrayBounds(taskbarHandle);
            var systemTray = IsInsideFrame(nativeTray, taskbarRect)
                ? nativeTray
                : IsInsideFrame(automationBounds.SystemTray, taskbarRect)
                    ? automationBounds.SystemTray
                    : null;
            var scale = host.Dpi / 96d;
            var physicalWidth = Math.Max(1, (int)Math.Round(LogicalWidth * scale));
            var desiredHeight = (int)Math.Round(LogicalHeight * scale);
            var physicalHeight = Math.Max(1, Math.Min(desiredHeight, frame.Height - (int)Math.Round(4 * scale)));
            if (_widgetSize != (physicalWidth, physicalHeight))
            {
                StopPlacementAnimation();
                _placementAnimation.Reset();
                _appliedLayout = null;
                _widgetSize = (physicalWidth, physicalHeight);
            }
            var gap = Math.Max(1, (int)Math.Round(
                (host.Alignment == TaskbarAlignment.Left ? RightAnchorGap : 2) * scale));
            var placement = TaskbarPlacement.Calculate(
                frame,
                widgets,
                systemTray,
                host.Alignment,
                physicalWidth,
                physicalHeight,
                gap,
                Math.Max(1, (int)Math.Round(12 * scale)));
            if (placement is not PixelPoint safePlacement)
            {
                _isAttached = false;
                _appliedLayout = null;
                HideWidget();
                return;
            }
            var handle = Handle;
            var parentChanged = NativeMethods.GetParent(handle) != taskbarHandle;
            if (!_isAttached || parentChanged)
            {
                _appliedLayout = null;
                ApplyChildWindowStyles(handle);
            }
            if (parentChanged)
            {
                NativeMethods.SetParent(handle, taskbarHandle);
                if (NativeMethods.GetParent(handle) != taskbarHandle)
                {
                    DetachAndHide();
                    return;
                }
            }
            var now = _placementClock.Elapsed;
            var animate = host.Alignment == TaskbarAlignment.Left &&
                _isAttached && !parentChanged && _appliedLayout is not null &&
                TaskbarPresentation.ShouldShow(_state);
            int? maximumX = host.Alignment == TaskbarAlignment.Left ? safePlacement.X + gap : null;
            if (host.Alignment == TaskbarAlignment.Left && !automationBounds.Succeeded &&
                _placementAnimation.GetPosition(now) is PixelPoint current)
            {
                // A missing Widgets sample must not send us right toward an unknown boundary.
                safePlacement = safePlacement with { X = Math.Min(current.X, safePlacement.X) };
            }
            _animationLayout = new AnimationLayout(host, widgets, physicalWidth, physicalHeight);
            _placementAnimation.MoveTo(safePlacement, now, animate, maximumX);
            if (!ApplyPlacement(_placementAnimation.GetPosition(now)!.Value, _animationLayout.Value))
            {
                DetachAndHide();
                return;
            }
            if (_placementAnimation.IsAnimating)
            {
                StartPlacementAnimation();
            }
            else
            {
                StopPlacementAnimation();
            }
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

    private bool ApplyPlacement(PixelPoint placement, AnimationLayout layout)
    {
        var host = layout.Host;
        var scale = host.Dpi / 96d;
        var clientPoint = new NativePoint { X = placement.X, Y = placement.Y };
        if (!NativeMethods.ScreenToClient(host.Handle, ref clientPoint))
        {
            return false;
        }

        var position = new PixelPoint(clientPoint.X, clientPoint.Y);
        var sizeChanged = _appliedLayout is not AppliedLayout previousSize ||
            previousSize.Width != host.Rect.Width || previousSize.Height != host.Rect.Height ||
            previousSize.Scale != scale;
        if (sizeChanged)
        {
            HostCanvas.Width = host.Rect.Width / scale;
            HostCanvas.Height = host.Rect.Height / scale;
            HostCanvas.UpdateLayout();
            if (!NativeMethods.SetWindowPos(
                Handle, nint.Zero, 0, 0, host.Rect.Width, host.Rect.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate |
                NativeMethods.SwpAsyncWindowPos | NativeMethods.SwpShowWindow))
            {
                return false;
            }
        }

        var hitRegion = TaskbarPlacement.CalculateHitRegion(
            host.Rect, layout.Widgets, placement, layout.WidgetWidth, layout.WidgetHeight, 0, 0);
        var clientRegion = new PixelRect(
            hitRegion.Left - host.Rect.Left, hitRegion.Top - host.Rect.Top,
            hitRegion.Right - host.Rect.Left, hitRegion.Bottom - host.Rect.Top);
        if (_appliedLayout is not AppliedLayout previousRegion || previousRegion.HitRegion != clientRegion)
        {
            var region = NativeMethods.CreateRectRgn(
                clientRegion.Left, clientRegion.Top, clientRegion.Right, clientRegion.Bottom);
            if (region == nint.Zero)
            {
                return false;
            }
            if (NativeMethods.SetWindowRgn(Handle, region, true) == 0)
            {
                NativeMethods.DeleteObject(region);
                return false;
            }
            // The system owns the region after SetWindowRgn succeeds.
        }

        if (_appliedLayout is not AppliedLayout previousPosition ||
            previousPosition.Position != position || previousPosition.Scale != scale)
        {
            // RenderTransform avoids a full WPF layout pass on each animation frame.
            _placementTransform.X = clientPoint.X / scale;
            _placementTransform.Y = clientPoint.Y / scale;
        }
        _appliedLayout = new AppliedLayout(position, host.Rect.Width, host.Rect.Height, scale, clientRegion);
        return true;
    }

    private void StartPlacementAnimation()
    {
        if (_isAnimatingPlacement)
        {
            return;
        }
        _isAnimatingPlacement = true;
        CompositionTarget.Rendering += PlacementAnimation_Rendering;
    }

    private void StopPlacementAnimation()
    {
        if (!_isAnimatingPlacement)
        {
            return;
        }
        CompositionTarget.Rendering -= PlacementAnimation_Rendering;
        _isAnimatingPlacement = false;
    }

    private void PlacementAnimation_Rendering(object? sender, EventArgs e)
    {
        try
        {
            if (_animationLayout is not AnimationLayout layout ||
                _placementAnimation.GetPosition(_placementClock.Elapsed) is not PixelPoint position)
            {
                StopPlacementAnimation();
                return;
            }
            if (!NativeMethods.IsWindow(layout.Host.Handle) ||
                !NativeMethods.GetWindowRect(layout.Host.Handle, out var rect) ||
                rect.ToPixelRect() != layout.Host.Rect ||
                NativeMethods.GetDpiForWindow(layout.Host.Handle) != layout.Host.Dpi ||
                !ApplyPlacement(position, layout))
            {
                DetachAndHide();
                return;
            }
            if (!_placementAnimation.IsAnimating)
            {
                StopPlacementAnimation();
            }
        }
        catch (Exception)
        {
            DetachAndHide();
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

    private async Task<AutomationBounds> GetAutomationBoundsAsync(
        nint taskbarHandle, CancellationToken cancellationToken)
    {
        if (_automationQuery is { IsCompleted: false })
        {
            // A timed-out query may still be running. Never apply its stale result or pile up queries.
            return default;
        }

        _automationQuery = Task.Run(() => QueryAutomationBounds(taskbarHandle));
        try
        {
            return await _automationQuery.WaitAsync(TimeSpan.FromMilliseconds(700), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return default;
        }
        catch (Exception)
        {
            _automationQuery = null;
            return default;
        }
    }

    private static AutomationBounds QueryAutomationBounds(nint taskbarHandle)
    {
        var root = AutomationElement.FromHandle(taskbarHandle);
        return new AutomationBounds(
            true,
            FindAutomationRect(root, "TaskbarFrame"),
            FindAutomationRect(root, "WidgetsButton"),
            FindAutomationRect(root, "SystemTrayFrame"));
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

    private static bool IsInsideFrame(PixelRect? candidate, PixelRect frame) =>
        candidate is { IsEmpty: false } rectangle &&
        rectangle.Left >= frame.Left &&
        rectangle.Top >= frame.Top &&
        rectangle.Right <= frame.Right &&
        rectangle.Bottom <= frame.Bottom;

    private static PixelRect? GetSystemTrayBounds(nint taskbarHandle)
    {
        var systemTrayHandle = NativeMethods.FindWindowEx(
            taskbarHandle,
            nint.Zero,
            "TrayNotifyWnd",
            null);
        return systemTrayHandle != nint.Zero &&
            NativeMethods.GetWindowRect(systemTrayHandle, out var systemTrayRect)
            ? systemTrayRect.ToPixelRect()
            : null;
    }

    private static HostContext? GetHostContext()
    {
        var handle = NativeMethods.FindWindow(TaskbarClassName, null);
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var nativeRect))
        {
            return null;
        }

        var rect = nativeRect.ToPixelRect();
        return rect.IsEmpty || rect.Height >= rect.Width
            ? null
            : new HostContext(handle, rect, Math.Max(96u, NativeMethods.GetDpiForWindow(handle)), GetTaskbarAlignment());
    }

    private static TaskbarAlignment GetTaskbarAlignment()
    {
        try
        {
            return Registry.GetValue(TaskbarAlignmentRegistryPath, "TaskbarAl", 1) is 0
                ? TaskbarAlignment.Left
                : TaskbarAlignment.Center;
        }
        catch (Exception)
        {
            return TaskbarAlignment.Center;
        }
    }

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
        ResetPlacement();
        _isAttached = false;
        HideWidget();
    }

    private void ResetPlacement()
    {
        StopPlacementAnimation();
        _placementAnimation.Reset();
        _animationLayout = null;
        _hostContext = null;
        _automationQuery = null;
        _automationFrame = null;
        _widgetSize = null;
        _appliedLayout = null;
    }

    private void HideWidget()
    {
        StopPlacementAnimation();
        _placementAnimation.Reset();
        _animationLayout = null;
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

    private void TextViewport_SizeChanged(object sender, SizeChangedEventArgs e)
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
        ScrollingArtistText.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var synchronizeSecondary = TaskbarPresentation.ShouldSynchronizeMarquee(_state);
        var contentWidth = TaskbarPresentation.CalculateMarqueeContentWidth(
            TitleText.DesiredSize.Width,
            ScrollingArtistText.DesiredSize.Width,
            synchronizeSecondary);
        var overflow = TaskbarPresentation.CalculateMarqueeDistance(contentWidth, TextViewport.ActualWidth);
        if (overflow <= 0 || _isPointerOver)
        {
            return;
        }

        TitleText.Width = contentWidth;
        MarqueeTitleText.Width = contentWidth;
        MarqueeTitleText.Visibility = Visibility.Visible;
        if (synchronizeSecondary)
        {
            ArtistText.Visibility = Visibility.Collapsed;
            ScrollingArtistText.Width = contentWidth;
            MarqueeArtistText.Width = contentWidth;
            SynchronizedSecondaryMarquee.Visibility = Visibility.Visible;
        }

        var cycleDistance = TaskbarPresentation.CalculateMarqueeCycleDistance(
            contentWidth,
            MarqueeGap);
        var travelDuration = TimeSpan.FromSeconds(cycleDistance / MarqueeSpeed);
        var cycleDuration = MarqueeStartDelay + travelDuration;
        ((TranslateTransform)PrimaryMarqueePanel.RenderTransform).BeginAnimation(
            TranslateTransform.XProperty,
            CreateMarqueeAnimation(cycleDistance, cycleDuration));
        if (synchronizeSecondary)
        {
            ((TranslateTransform)SecondaryMarqueePanel.RenderTransform).BeginAnimation(
                TranslateTransform.XProperty,
                CreateMarqueeAnimation(cycleDistance, cycleDuration));
        }
    }

    private static DoubleAnimationUsingKeyFrames CreateMarqueeAnimation(
        double cycleDistance,
        TimeSpan cycleDuration)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(MarqueeStartDelay)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            -cycleDistance,
            KeyTime.FromTimeSpan(cycleDuration)));
        return animation;
    }

    private void StopMarquee()
    {
        var primaryTransform = (TranslateTransform)PrimaryMarqueePanel.RenderTransform;
        primaryTransform.BeginAnimation(TranslateTransform.XProperty, null);
        primaryTransform.X = 0;
        var secondaryTransform = (TranslateTransform)SecondaryMarqueePanel.RenderTransform;
        secondaryTransform.BeginAnimation(TranslateTransform.XProperty, null);
        secondaryTransform.X = 0;
        MarqueeTitleText.Visibility = Visibility.Collapsed;
        ArtistText.Visibility = Visibility.Visible;
        SynchronizedSecondaryMarquee.Visibility = Visibility.Collapsed;
        TitleText.Width = double.NaN;
        MarqueeTitleText.Width = double.NaN;
        ScrollingArtistText.Width = double.NaN;
        MarqueeArtistText.Width = double.NaN;
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
