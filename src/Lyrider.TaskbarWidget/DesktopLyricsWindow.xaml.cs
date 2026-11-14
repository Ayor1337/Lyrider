using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using FormsScreen = System.Windows.Forms.Screen;
using Size = System.Windows.Size;

namespace Lyrider.TaskbarWidget;

public partial class DesktopLyricsWindow : Window
{
    private DesktopLyricsOptions _options = new();
    private DesktopLyricsState _state = DesktopLyricsState.Unavailable;
    private bool _placing;
    private DesktopLyricsResizeEdge _resizeEdges;
    private bool _resizeAutoHeight;
    private DesktopLyricsResizeEdge _hoverEdges;
    private readonly HashSet<FrameworkElement> _shownResizeIndicators = [];
    private readonly SolidColorBrush _backdropBrush = new(Colors.Transparent);
    private Color _backdropTarget = Colors.Transparent;
    private bool _toolbarShown;
    private double _resizeStartHeight;
    private bool _moving;
    private OutlinedLyricText PrimaryText => LyricsContent.PrimaryText;
    private OutlinedLyricText SecondaryText => LyricsContent.SecondaryText;
    private double _extraLineGap;
    private bool _currentInSecondary;
    private HwndSource? _source;
    private bool _karaokeAnimating;
    private Color _highlightColor = (Color)ColorConverter.ConvertFromString(new DesktopLyricsOptions().HighlightColor);
    private readonly DispatcherTimer _lockedHoverTimer;
    private DesktopLyricsUnlockWindow? _unlockWindow;
    private DispatcherOperation? _revealOperation;
    private readonly ContextMenu _quickMenu;

    public event Action<DesktopLyricsCommand>? CommandRequested;
    public event Action<DesktopLyricsPosition>? PositionChanged;

    private nint Handle => new WindowInteropHelper(this).Handle;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        RegisterName("PrimaryText", PrimaryText);
        RegisterName("SecondaryText", SecondaryText);
        _quickMenu = CreateQuickMenu();
        Backdrop.Background = _backdropBrush;
        Surface.ContextMenu = _quickMenu;
        _lockedHoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _lockedHoverTimer.Tick += LockedHoverTimer_Tick;
        PreviousButton.ToolTip = WidgetText.Get("上一首", "Previous");
        NextButton.ToolTip = WidgetText.Get("下一首", "Next");
        AutomationProperties.SetName(PreviousButton, (string)PreviousButton.ToolTip);
        AutomationProperties.SetName(NextButton, (string)NextButton.ToolTip);
        LockButton.ToolTip = WidgetText.Get("锁定", "Lock");
        CloseButton.ToolTip = WidgetText.Get("关闭歌词", "Close lyrics");
        AutomationProperties.SetName(LockButton, (string)LockButton.ToolTip);
        AutomationProperties.SetName(CloseButton, (string)CloseButton.ToolTip);
        SourceInitialized += Window_SourceInitialized;
        SizeChanged += (_, _) =>
        {
            if (_resizeEdges != DesktopLyricsResizeEdge.None) UpdateResizingLayout();
            else if (!_placing && Handle != 0) PlaceWindow();
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _quickMenu.IsOpen = false;
            _revealOperation?.Abort();
            StopKaraokeAnimation();
            _lockedHoverTimer.Stop();
            _lockedHoverTimer.Tick -= LockedHoverTimer_Tick;
            CloseUnlockWindow();
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _source?.RemoveHook(WindowHook);
        };
    }

    public void ApplyOptions(DesktopLyricsOptions options)
    {
        options = options.Normalize();
        if (_options == options) return;
        _options = options;
        _highlightColor = (Color)ColorConverter.ConvertFromString(options.HighlightColor);
        if (options.Locked) _hoverEdges = DesktopLyricsResizeEdge.None;
        UpdateResizeIndicators();
        if (options.Locked || !options.Enabled) _quickMenu.IsOpen = false;
        Surface.ContextMenu = options.Locked ? null : _quickMenu;
        UpdateQuickMenu();
        ApplyInputStyle();
        UpdateContent();
        if (Handle != 0)
        {
            PlaceWindow();
        }
    }

    public void SetPlaybackState(DesktopLyricsState state)
    {
        if (_state == state) return;
        var contentChanged = !DesktopLyricsPresentation.HasSameContent(_state, state);
        _state = state;
        // 每秒刷新只会移动逐字时间锚点，此时只更新扫色，不重排、不移动窗口。
        if (contentChanged) UpdateContent();
        else UpdateKaraokeProgress();
    }

    public void ResetPosition()
    {
        _options = _options with { Position = null };
        PlaceWindow();
    }

    private void UpdateContent()
    {
        PlayPauseIcon.Text = TaskbarPresentation.GetPlayPauseGlyph(_state.IsPlaying);
        PlayPauseButton.ToolTip = _state.IsPlaying ? WidgetText.Get("暂停", "Pause") : WidgetText.Get("播放", "Play");
        AutomationProperties.SetName(PlayPauseButton, (string)PlayPauseButton.ToolTip);
        var text = LyricsContent.Apply(_state, _options);
        _currentInSecondary = text.CurrentInSecondary;
        _extraLineGap = 0;
        PrimaryText.ToolTip = _options.Locked ? null : text.Primary;
        SecondaryText.ToolTip = _options.Locked ? null : text.Secondary;
        ResizeFrame.Visibility = _options.Locked ? Visibility.Collapsed : Visibility.Visible;
        UpdateHover();
        if (text.IsVisible)
        {
            if (!IsVisible)
            {
                Show();
            }
            PlaceWindow();
        }
        else if (IsVisible)
        {
            _quickMenu.IsOpen = false;
            _revealOperation?.Abort();
            _revealOperation = null;
            Opacity = 0;
            Hide();
        }
        UpdateKaraokeProgress();
        UpdateLockedHoverTracking();
        if (text.IsVisible && Opacity == 0) RevealAfterRender();
    }

    private void RevealAfterRender()
    {
        _revealOperation?.Abort();
        // 等布局与渲染队列处理完成，再显示首帧。
        _revealOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _revealOperation = null;
            if (!IsVisible || !DesktopLyricsPresentation.Select(_state, _options).IsVisible) return;
            Opacity = 1;
            UpdateLockedHoverTracking();
        }));
    }

    private void KaraokeRendering(object? sender, EventArgs e) => UpdateKaraokeProgress();

    // 扫色跟随渲染帧推进，避免固定间隔计时器与屏幕刷新不同步造成的跳格。
    private void StartKaraokeAnimation()
    {
        if (_karaokeAnimating) return;
        _karaokeAnimating = true;
        CompositionTarget.Rendering += KaraokeRendering;
    }

    private void StopKaraokeAnimation()
    {
        if (!_karaokeAnimating) return;
        _karaokeAnimating = false;
        CompositionTarget.Rendering -= KaraokeRendering;
    }

    private void UpdateKaraokeProgress()
    {
        var timing = _options.KaraokeEnabled && IsVisible && !DesktopLyricsPresentation.UsesTranslation(_state, _options) &&
            !string.IsNullOrWhiteSpace(_state.CurrentLyric)
            ? _state.Timing : null;
        var progress = timing?.ProgressAt(Stopwatch.GetElapsedTime(timing.ObservedTimestamp).TotalSeconds, _state.IsPlaying) ?? 0;
        PrimaryText.SetKaraokeProgress(_currentInSecondary ? 0 : progress, _highlightColor);
        SecondaryText.SetKaraokeProgress(_currentInSecondary ? progress : 0, _highlightColor);
        if (timing is not null && _state.IsPlaying && progress < 1)
            StartKaraokeAnimation();
        else
            StopKaraokeAnimation();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(Handle);
        _source?.AddHook(WindowHook);
        ApplyInputStyle();
        PlaceWindow();
    }

    private void ApplyInputStyle()
    {
        if (Handle == 0) return;
        var style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GwlExStyle).ToInt64();
        style = (style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate) & ~NativeMethods.WsExAppWindow;
        style = _options.Locked ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlExStyle, new nint(style));
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (message == NativeMethods.WmSizing && _resizeEdges != DesktopLyricsResizeEdge.None && lParam != 0)
        {
            var rect = Marshal.PtrToStructure<NativeRect>(lParam);
            // 以系统报告的实际拖动边为准，键盘缩放时用户可能改选其他边。
            var edges = EdgesFromSizingDirection((int)wParam);
            if (edges != DesktopLyricsResizeEdge.None && edges != _resizeEdges)
            {
                _resizeEdges = edges;
                UpdateResizeIndicators();
            }
            var constrained = ConstrainResize(rect.ToPixelRect());
            Marshal.StructureToPtr(new NativeRect
            {
                Left = constrained.Left, Top = constrained.Top, Right = constrained.Right, Bottom = constrained.Bottom
            }, lParam, false);
            handled = true;
            return 1;
        }
        if (message == 0x02E0 || message == 0x001A) // DPI or work-area change
        {
            Dispatcher.BeginInvoke(PlaceWindow);
        }
        return 0;
    }

    private void PlaceWindow()
    {
        if (_placing || _resizeEdges != DesktopLyricsResizeEdge.None || _moving || Handle == 0) return;
        _placing = true;
        try
        {
            var monitor = FormsScreen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _options.Position?.Monitor)
                ?? FormsScreen.PrimaryScreen ?? FormsScreen.AllScreens[0];
            var work = monitor.WorkingArea;
            var scale = ResolveScale(monitor);
            var area = new PixelRect(work.Left, work.Top, work.Right, work.Bottom);
            var position = monitor.DeviceName == _options.Position?.Monitor ? _options.Position : null;
            // 暂停自动高度，先测量内容，再一次提交最终尺寸和位置。
            SizeToContent = SizeToContent.Manual;
            var width = DesktopLyricsPlacement.ConstrainSize(position?.Width ?? 960, DesktopLyricsPlacement.MinimumHeight,
                work.Width / scale, work.Height / scale).Width;
            var minimumHeight = Math.Min(MinimumContentHeight(width), work.Height / scale);
            var height = DesktopLyricsPlacement.ConstrainSize(width, position?.Height ?? minimumHeight,
                work.Width / scale, work.Height / scale, minimumHeight).Height;
            var rect = DesktopLyricsPlacement.Calculate(area, scale, height, position);
            MinWidth = 0;
            MinHeight = 0;
            NativeMethods.SetWindowPos(Handle, new nint(-1), rect.Left, rect.Top, rect.Width, rect.Height,
                NativeMethods.SwpNoActivate);
            MaxWidth = Math.Min(DesktopLyricsPlacement.MaximumWidth, work.Width / scale);
            MinWidth = Math.Min(DesktopLyricsPlacement.MinimumWidth, MaxWidth);
            MaxHeight = Math.Min(DesktopLyricsPlacement.MaximumHeight, work.Height / scale);
            MinHeight = Math.Min(minimumHeight, MaxHeight);
            UpdateLayout();
            UpdateLineSpacing();
            UpdateLayout();
            if (position?.Height is null) SizeToContent = SizeToContent.Height;
            RefreshLockedHover();
        }
        finally
        {
            _placing = false;
        }
    }

    private static double ResolveScale(FormsScreen screen)
    {
        var point = new NativePoint { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        return NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0 : 1;
    }

    private void SavePosition(bool? saveHeight = null)
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect)) return;
        var screen = FormsScreen.FromHandle(Handle);
        var work = screen.WorkingArea;
        var position = DesktopLyricsPlacement.Capture(screen.DeviceName,
            new(work.Left, work.Top, work.Right, work.Bottom), rect.ToPixelRect(), ResolveScale(screen),
            saveHeight: saveHeight ?? SizeToContent == SizeToContent.Manual);
        _options = _options with { Position = position };
        PlaceWindow();
        PositionChanged?.Invoke(position);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_options.Locked || e.OriginalSource is not DependencyObject target) return;
        for (var parent = target; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ButtonBase) return;
        }
        try
        {
            _moving = true;
            DragMove();
            _moving = false;
            SavePosition();
        }
        catch (InvalidOperationException)
        {
            // 松开鼠标后到达的消息不再启动拖动。
        }
        finally
        {
            _moving = false;
        }
    }

    private void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_options.Locked) return;
        _quickMenu.PlacementTarget = Surface;
        _quickMenu.Placement = PlacementMode.MousePoint;
        _quickMenu.IsOpen = true;
        e.Handled = true;
    }

    // 边框交给系统缩放循环处理：拖动跟手、支持八个方向，尺寸约束在 WM_SIZING 中统一计算。
    private void ResizeEdge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_options.Locked || sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<DesktopLyricsResizeEdge>(tag, out var edges) || edges == DesktopLyricsResizeEdge.None) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            // 双击上下边恢复自动高度，保留当前宽度和位置。
            if (edges.HasFlag(DesktopLyricsResizeEdge.Top) || edges.HasFlag(DesktopLyricsResizeEdge.Bottom))
                RestoreAutoHeight();
            return;
        }
        if (e.ButtonState != MouseButtonState.Pressed) return;
        BeginResize(edges);
        try
        {
            Mouse.Capture(null);
            NativeMethods.SendMessage(Handle, NativeMethods.WmSysCommand, NativeMethods.ScSize + SizingDirection(edges), 0);
        }
        finally
        {
            EndResize();
        }
    }

    private static int SizingDirection(DesktopLyricsResizeEdge edges) => edges switch
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

    private static DesktopLyricsResizeEdge EdgesOf(object element) =>
        element is FrameworkElement { Tag: string tag } && Enum.TryParse<DesktopLyricsResizeEdge>(tag, out var edges)
            ? edges : DesktopLyricsResizeEdge.None;

    private void ResizeEdge_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverEdges = EdgesOf(sender);
        UpdateResizeIndicators();
    }

    private void ResizeEdge_MouseLeave(object sender, MouseEventArgs e)
    {
        // 相邻的边和角互相重叠，离开与进入的顺序不固定，只清除自己设置的悬停状态。
        if (_hoverEdges == EdgesOf(sender)) _hoverEdges = DesktopLyricsResizeEdge.None;
        UpdateResizeIndicators();
    }

    // 拖动中以实际缩放的边为准，只显示与之完全对应的那个指示器。
    private void UpdateResizeIndicators()
    {
        var active = _options.Locked ? DesktopLyricsResizeEdge.None
            : _resizeEdges != DesktopLyricsResizeEdge.None ? _resizeEdges : _hoverEdges;
        foreach (var indicator in ResizeIndicators.Children.OfType<FrameworkElement>())
            SetResizeIndicatorVisible(indicator, active != DesktopLyricsResizeEdge.None && EdgesOf(indicator) == active);
    }

    // 显示时淡入并从 60% 放大到原尺寸，隐藏时稍慢淡出缩回；角上的弧以窗口角为原点展开。
    private void SetResizeIndicatorVisible(FrameworkElement indicator, bool visible)
    {
        if (visible == _shownResizeIndicators.Contains(indicator)) return;
        if (visible) _shownResizeIndicators.Add(indicator);
        else _shownResizeIndicators.Remove(indicator);
        if (indicator.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
            indicator.RenderTransform = scale = new ScaleTransform(0.6, 0.6);
        double opacity = visible ? 1 : 0, size = visible ? 1 : 0.6;
        if (!SystemParameters.ClientAreaAnimation)
        {
            // 系统关闭了动画时直接切换。
            indicator.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            indicator.Opacity = opacity;
            scale.ScaleX = scale.ScaleY = size;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(visible ? 160 : 200);
        var easing = visible ? ResizeIndicatorShowEasing : ResizeIndicatorHideEasing;
        indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) { EasingFunction = easing });
        var grow = new DoubleAnimation(size, duration) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static readonly IEasingFunction ResizeIndicatorShowEasing = FrozenEasing(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
    private static readonly IEasingFunction ResizeIndicatorHideEasing = FrozenEasing(new CubicEase { EasingMode = EasingMode.EaseIn });

    private static IEasingFunction FrozenEasing(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }

    private static DesktopLyricsResizeEdge EdgesFromSizingDirection(int direction) => direction switch
    {
        1 => DesktopLyricsResizeEdge.Left,
        2 => DesktopLyricsResizeEdge.Right,
        3 => DesktopLyricsResizeEdge.Top,
        4 => DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Left,
        5 => DesktopLyricsResizeEdge.Top | DesktopLyricsResizeEdge.Right,
        6 => DesktopLyricsResizeEdge.Bottom,
        7 => DesktopLyricsResizeEdge.Bottom | DesktopLyricsResizeEdge.Left,
        8 => DesktopLyricsResizeEdge.Bottom | DesktopLyricsResizeEdge.Right,
        _ => DesktopLyricsResizeEdge.None
    };

    private void BeginResize(DesktopLyricsResizeEdge edges)
    {
        _resizeEdges = edges;
        _resizeAutoHeight = SizeToContent == SizeToContent.Height;
        _resizeStartHeight = ActualHeight;
        UpdateResizeIndicators();
        // 缩放期间由 WM_SIZING 决定尺寸，关闭自动高度和 WPF 自身的最小最大值限制，避免互相拉扯。
        SizeToContent = SizeToContent.Manual;
        MinWidth = MinHeight = 0;
        MaxWidth = MaxHeight = double.PositiveInfinity;
    }

    private void EndResize()
    {
        if (_resizeEdges == DesktopLyricsResizeEdge.None) return;
        // 拖了上下边但高度没变（例如按 Esc 取消）时保持原来的自动高度。
        var changedHeight = (_resizeEdges.HasFlag(DesktopLyricsResizeEdge.Top) || _resizeEdges.HasFlag(DesktopLyricsResizeEdge.Bottom)) &&
            Math.Abs(ActualHeight - _resizeStartHeight) > 0.5;
        _resizeEdges = DesktopLyricsResizeEdge.None;
        _hoverEdges = DesktopLyricsResizeEdge.None;
        UpdateResizeIndicators();
        // 先按当前矩形保存，再由 PlaceWindow 按是否保存了高度恢复自动高度；
        // 先切换 SizeToContent 会触发按旧位置重新放置，丢掉这次缩放。
        SavePosition(saveHeight: !_resizeAutoHeight || changedHeight);
    }

    private void RestoreAutoHeight()
    {
        // 先去掉保存的高度再放置窗口；直接切换 SizeToContent 会触发按旧高度重新放置。
        if (_options.Position is not { Height: not null } position) return;
        var updated = position with { Height = null };
        _options = _options with { Position = updated };
        PlaceWindow();
        PositionChanged?.Invoke(updated);
    }

    private PixelRect ConstrainResize(PixelRect proposed)
    {
        var screen = FormsScreen.FromHandle(Handle);
        var work = screen.WorkingArea;
        var vertical = _options.TextDirection == DesktopLyricsTextDirection.Vertical;
        // 竖排文字会按高度截断，最小高度只需容纳两个字；横排至少容纳当前宽度下的全部歌词。
        double MinimumHeight(double width) => vertical
            ? DesktopLyricsContent.WindowVerticalPadding + LyricsContent.Margin.Top + LyricsContent.Margin.Bottom + _options.FontSize * 2.5
            : MinimumContentHeight(width);
        return DesktopLyricsPlacement.Resize(proposed, _resizeEdges, new(work.Left, work.Top, work.Right, work.Bottom),
            ResolveScale(screen), MinimumHeight, _resizeAutoHeight ? width => MinimumContentHeight(width) : null);
    }

    private void UpdateResizingLayout()
    {
        if (_options.TextDirection == DesktopLyricsTextDirection.Vertical)
        {
            PrimaryText.MaxHeight = SecondaryText.MaxHeight = Math.Max(1, ActualHeight -
                DesktopLyricsContent.WindowVerticalPadding - LyricsContent.Margin.Top - LyricsContent.Margin.Bottom);
        }
        UpdateLayout();
        UpdateLineSpacing();
    }

    private double MinimumContentHeight(double? width = null)
    {
        LyricsContent.Measure(new Size(Math.Max(1, (width ?? (ActualWidth > 0 ? ActualWidth : Width)) - 24), double.PositiveInfinity));
        return Math.Max(DesktopLyricsPlacement.MinimumHeight, LyricsContent.DesiredSize.Height + DesktopLyricsContent.WindowVerticalPadding - _extraLineGap);
    }

    private void UpdateLineSpacing()
    {
        if (_options.TextDirection == DesktopLyricsTextDirection.Vertical) return;
        var extra = SizeToContent == SizeToContent.Manual && SecondaryText.Visibility == Visibility.Visible
            ? Math.Max(0, ActualHeight - MinimumContentHeight()) * 0.2 : 0;
        if (Math.Abs(extra - _extraLineGap) < 0.1) return;
        _extraLineGap = extra;
        LyricsContent.SetExtraLineGap(extra);
    }

    private void UpdateHover()
    {
        var hover = !_options.Locked && (Surface.IsMouseOver || _quickMenu.IsOpen);
        // 锁定与解锁切换时也渐变；锁定后的鼠标穿透在 ApplyInputStyle 中立即生效。
        var animate = IsVisible && Opacity > 0;
        SetToolbarVisible(hover, animate);
        var alpha = _options.Locked ? (byte)0 : (byte)Math.Round(255 * Math.Max(_options.BackgroundOpacity, hover ? 0.45 : 0));
        // 窗口尚未显示时直接切换，首帧不应从透明渐变出来。
        SetBackdropAlpha(alpha, animate);
        Surface.Background = _options.Locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

    // 与缩放指示器节奏一致：出现 160ms 减速，消失 200ms 加速；不用回弹，避免透明度过冲。
    private void SetBackdropAlpha(byte alpha, bool animate)
    {
        var target = Color.FromArgb(alpha, 0, 0, 0);
        if (target == _backdropTarget) return;
        var showing = alpha > _backdropBrush.Color.A;
        _backdropTarget = target;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _backdropBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _backdropBrush.Color = target;
            return;
        }
        _backdropBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(showing ? 160 : 200))
            {
                EasingFunction = showing ? BackdropShowEasing : ResizeIndicatorHideEasing
            });
    }

    // 工具栏与背景同节奏淡入淡出；动画只作用在外层胶囊的不透明度上，静止时仍以 Visibility 表示显隐。
    private void SetToolbarVisible(bool visible, bool animate)
    {
        if (animate && visible == _toolbarShown) return;
        var wasHidden = Toolbar.Visibility != Visibility.Visible;
        _toolbarShown = visible;
        // 淡出期间按钮已不可点击，避免误触正在消失的按钮。
        ToolbarSurface.IsHitTestVisible = visible;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            ToolbarSurface.BeginAnimation(OpacityProperty, null);
            ToolbarSurface.Opacity = 1;
            Toolbar.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            return;
        }
        var animation = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 160 : 200))
        {
            EasingFunction = visible ? BackdropShowEasing : ResizeIndicatorHideEasing
        };
        if (visible)
        {
            // 从完全隐藏开始时从透明淡入；若正在淡出则从当前不透明度接着淡入。
            // 原值也设为 0：动画时钟要到下一帧才开始，否则显示的第一帧会闪出完全不透明的工具栏。
            if (wasHidden)
            {
                animation.From = 0;
                ToolbarSurface.Opacity = 0;
            }
            Toolbar.Visibility = Visibility.Visible;
        }
        else
        {
            animation.Completed += (_, _) =>
            {
                if (_toolbarShown) return;
                Toolbar.Visibility = Visibility.Hidden;
                ToolbarSurface.BeginAnimation(OpacityProperty, null);
                ToolbarSurface.Opacity = 1;
            };
        }
        ToolbarSurface.BeginAnimation(OpacityProperty, animation);
    }

    private static readonly IEasingFunction BackdropShowEasing = FrozenEasing(new CubicEase { EasingMode = EasingMode.EaseOut });

    private void UpdateLockedHoverTracking()
    {
        if (_options.Locked && IsVisible)
        {
            _lockedHoverTimer.Start();
            RefreshLockedHover();
        }
        else
        {
            _lockedHoverTimer.Stop();
            if (_options.Locked) _unlockWindow?.Hide();
            else CloseUnlockWindow();
        }
    }

    private void LockedHoverTimer_Tick(object? sender, EventArgs e) => RefreshLockedHover();

    private void RefreshLockedHover()
    {
        if (NativeMethods.GetCursorPos(out var point)) UpdateLockedHover(new(point.X, point.Y));
        else _unlockWindow?.FadeOut();
    }

    private void UpdateLockedHover(PixelPoint point)
    {
        if (!_options.Locked || !IsVisible || !NativeMethods.GetWindowRect(Handle, out var rect) ||
            point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
        {
            // 歌词窗口本身已隐藏时立即隐藏图标；只是鼠标移开时淡出。
            if (!IsVisible) _unlockWindow?.Hide();
            else _unlockWindow?.FadeOut();
            return;
        }
        if (_unlockWindow is null)
        {
            var unlock = new DesktopLyricsUnlockWindow { Owner = this };
            unlock.UnlockRequested += UnlockWindow_UnlockRequested;
            unlock.Closed += (_, _) => { if (ReferenceEquals(_unlockWindow, unlock)) _unlockWindow = null; };
            _unlockWindow = unlock;
        }
        _unlockWindow.ShowAt(Handle, rect, new Rect(LockButton.TranslatePoint(new System.Windows.Point(), this), LockButton.RenderSize));
    }

    private void UnlockWindow_UnlockRequested() => CommandRequested?.Invoke(DesktopLyricsCommand.ToggleLocked);

    private void CloseUnlockWindow()
    {
        if (_unlockWindow is not { } unlock) return;
        _unlockWindow = null;
        unlock.UnlockRequested -= UnlockWindow_UnlockRequested;
        unlock.Close();
    }

    private ContextMenu CreateQuickMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("QuickMenuStyle") };
        menu.Resources[typeof(MenuItem)] = FindResource(typeof(MenuItem));
        menu.Resources[typeof(Separator)] = FindResource(typeof(Separator));
        AutomationProperties.SetName(menu, WidgetText.Get("桌面歌词快捷设置", "Desktop lyrics quick settings"));
        MenuItem Item(string chinese, string english, DesktopLyricsCommand command, bool checkable = false)
        {
            var item = new MenuItem { Header = WidgetText.Get(chinese, english), Tag = command, IsCheckable = checkable };
            AutomationProperties.SetName(item, (string)item.Header);
            item.Click += (_, e) =>
            {
                e.Handled = true;
                // 保存成功后才更新选择状态。
                UpdateQuickMenu();
                menu.IsOpen = false;
                CommandRequested?.Invoke(command);
            };
            return item;
        }
        MenuItem Group(string chinese, string english, params MenuItem[] items)
        {
            var group = new MenuItem { Header = WidgetText.Get(chinese, english) };
            AutomationProperties.SetName(group, (string)group.Header);
            foreach (var item in items) group.Items.Add(item);
            return group;
        }
        menu.Items.Add(Item("放大字号", "Increase font size", DesktopLyricsCommand.IncreaseFontSize));
        menu.Items.Add(Item("缩小字号", "Decrease font size", DesktopLyricsCommand.DecreaseFontSize));
        menu.Items.Add(new Separator());
        var alignment = Group("对齐方式", "Alignment",
            Item("居中", "Center", DesktopLyricsCommand.AlignCenter, true),
            Item("左右分离", "Split", DesktopLyricsCommand.AlignSplit, true),
            Item("左对齐", "Left", DesktopLyricsCommand.AlignLeft, true),
            Item("右对齐", "Right", DesktopLyricsCommand.AlignRight, true));
        alignment.ToolTip = WidgetText.Get("单行或带翻译时，左右分离按居中显示。", "Split is centered for single lines or translated lyrics.");
        menu.Items.Add(alignment);
        var lines = Group("单双行", "Line count",
            Item("单行", "Single line", DesktopLyricsCommand.SingleLine, true),
            Item("双行", "Double line", DesktopLyricsCommand.DoubleLine, true));
        lines.ToolTip = WidgetText.Get("带翻译时只显示当前原文和译文。", "Translated lyrics show only the current line and its translation.");
        menu.Items.Add(lines);
        menu.Items.Add(Group("文字方向", "Text direction",
            Item("横排", "Horizontal", DesktopLyricsCommand.HorizontalText, true),
            Item("竖排", "Vertical", DesktopLyricsCommand.VerticalText, true)));
        menu.Items.Add(Item("显示翻译", "Show translation", DesktopLyricsCommand.ToggleTranslation, true));
        menu.Items.Add(Item("逐字显示", "Word-by-word display", DesktopLyricsCommand.ToggleKaraoke, true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("锁定位置", "Lock position", DesktopLyricsCommand.ToggleLocked));
        menu.Items.Add(Item("重置位置", "Reset position", DesktopLyricsCommand.ResetPosition));
        menu.Items.Add(Item("关闭歌词", "Close lyrics", DesktopLyricsCommand.ToggleEnabled));
        menu.Opened += (_, _) => { UpdateQuickMenu(); UpdateHover(); };
        menu.Closed += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsSubmenuOpen = false;
            UpdateHover();
        };
        return menu;
    }

    private void UpdateQuickMenu()
    {
        void UpdateItems(ItemCollection items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                if (item.Tag is DesktopLyricsCommand command)
                {
                    item.IsChecked = command switch
                    {
                        DesktopLyricsCommand.AlignCenter => _options.EffectiveAlignment == DesktopLyricsAlignment.Center,
                        DesktopLyricsCommand.AlignSplit => _options.EffectiveAlignment == DesktopLyricsAlignment.Split,
                        DesktopLyricsCommand.AlignLeft => _options.EffectiveAlignment == DesktopLyricsAlignment.Left,
                        DesktopLyricsCommand.AlignRight => _options.EffectiveAlignment == DesktopLyricsAlignment.Right,
                        DesktopLyricsCommand.SingleLine => !_options.ShowDoubleLine,
                        DesktopLyricsCommand.DoubleLine => _options.ShowDoubleLine,
                        DesktopLyricsCommand.HorizontalText => _options.TextDirection == DesktopLyricsTextDirection.Horizontal,
                        DesktopLyricsCommand.VerticalText => _options.TextDirection == DesktopLyricsTextDirection.Vertical,
                        DesktopLyricsCommand.ToggleTranslation => _options.ShowTranslation,
                        DesktopLyricsCommand.ToggleKaraoke => _options.KaraokeEnabled,
                        _ => false
                    };
                    item.IsEnabled = command switch
                    {
                        DesktopLyricsCommand.IncreaseFontSize => _options.FontSize < 72,
                        DesktopLyricsCommand.DecreaseFontSize => _options.FontSize > 16,
                        _ => true
                    };
                    if (command is DesktopLyricsCommand.IncreaseFontSize or DesktopLyricsCommand.DecreaseFontSize)
                    {
                        var label = command == DesktopLyricsCommand.IncreaseFontSize
                            ? WidgetText.Get("放大字号", "Increase font size") : WidgetText.Get("缩小字号", "Decrease font size");
                        item.Header = $"{label} ({_options.FontSize:0.#})";
                        AutomationProperties.SetName(item, (string)item.Header);
                    }
                }
                UpdateItems(item.Items);
            }
        }
        UpdateItems(_quickMenu.Items);
    }

    private void Surface_MouseEnter(object sender, MouseEventArgs e) => UpdateHover();
    private void Surface_MouseLeave(object sender, MouseEventArgs e) => UpdateHover();
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(PlaceWindow);
    private void PreviousButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.Previous);
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.TogglePlayPause);
    private void NextButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.Next);
    private void LockButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.ToggleLocked);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.ToggleEnabled);
}
