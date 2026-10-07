using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using FormsScreen = System.Windows.Forms.Screen;
using Size = System.Windows.Size;

namespace Lyrider.TaskbarWidget;

public partial class DesktopLyricsWindow : Window
{
    private DesktopLyricsOptions _options = new();
    private DesktopLyricsState _state = DesktopLyricsState.Unavailable;
    private bool _placing;
    private bool _resizing;
    private bool _moving;
    private double _extraLineGap;
    private bool _currentInSecondary;
    private HwndSource? _source;
    private readonly DispatcherTimer _karaokeTimer;
    private readonly DispatcherTimer _lockedHoverTimer;
    private DesktopLyricsUnlockWindow? _unlockWindow;

    public event Action<DesktopLyricsCommand>? CommandRequested;
    public event Action<DesktopLyricsPosition>? PositionChanged;

    private nint Handle => new WindowInteropHelper(this).Handle;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        _karaokeTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _karaokeTimer.Tick += KaraokeTimer_Tick;
        _lockedHoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _lockedHoverTimer.Tick += LockedHoverTimer_Tick;
        LockButton.ToolTip = WidgetText.Get("锁定", "Lock");
        SettingsButton.ToolTip = WidgetText.Get("设置", "Settings");
        CloseButton.ToolTip = WidgetText.Get("关闭歌词", "Close lyrics");
        AutomationProperties.SetName(LockButton, (string)LockButton.ToolTip);
        AutomationProperties.SetName(SettingsButton, (string)SettingsButton.ToolTip);
        AutomationProperties.SetName(CloseButton, (string)CloseButton.ToolTip);
        SourceInitialized += Window_SourceInitialized;
        SizeChanged += (_, _) => { if (!_placing && !_resizing && Handle != 0) PlaceWindow(); };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _karaokeTimer.Stop();
            _karaokeTimer.Tick -= KaraokeTimer_Tick;
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
        _state = state;
        UpdateContent();
    }

    public void ResetPosition()
    {
        _options = _options with { Position = null };
        PlaceWindow();
    }

    private void UpdateContent()
    {
        var text = DesktopLyricsPresentation.Select(_state, _options);
        _currentInSecondary = text.CurrentInSecondary;
        var color = (Color)ColorConverter.ConvertFromString(_options.TextColor);
        var wordSync = _options.KaraokeEnabled && _state.Timing is { } timing && !timing.Words.IsDefaultOrEmpty;
        var currentColor = !string.IsNullOrWhiteSpace(_state.CurrentLyric) && !wordSync
            ? (Color)ColorConverter.ConvertFromString(_options.HighlightColor) : color;
        var staggered = _options.Layout == DesktopLyricsLayout.Horizontal;
        PrimaryText.SetText(text.Primary, _options.FontSize, _currentInSecondary ? color : currentColor,
            staggered ? TextAlignment.Left : TextAlignment.Center);
        PrimaryText.MinHeight = _currentInSecondary && text.Primary.Length == 0 ? _options.FontSize * 1.25 + 4 : 0;
        SecondaryText.SetText(text.Secondary ?? string.Empty, _options.FontSize, _currentInSecondary ? currentColor : color,
            staggered ? TextAlignment.Right : TextAlignment.Center);
        SecondaryText.Visibility = text.Secondary is null ? Visibility.Collapsed : Visibility.Visible;
        LyricsContent.VerticalAlignment = VerticalAlignment.Center;
        LyricsContent.Margin = staggered ? new Thickness(20, 12, 20, 12) : new Thickness(0);
        SecondaryRow.Height = GridLength.Auto;
        _extraLineGap = 0;
        SecondaryText.Margin = staggered ? new Thickness(4, 12, 4, 8) : new Thickness(4, 4, 4, 8);
        PrimaryText.VerticalAlignment = staggered ? VerticalAlignment.Top : VerticalAlignment.Center;
        SecondaryText.VerticalAlignment = staggered ? VerticalAlignment.Top : VerticalAlignment.Center;
        PrimaryText.ToolTip = _options.Locked ? null : text.Primary;
        SecondaryText.ToolTip = _options.Locked ? null : text.Secondary;
        WidthHandle.Visibility = _options.Locked ? Visibility.Collapsed : Visibility.Visible;
        HeightHandle.Visibility = WidthHandle.Visibility;
        CornerHandle.Visibility = WidthHandle.Visibility;
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
            Hide();
        }
        UpdateKaraokeProgress();
        UpdateLockedHoverTracking();
    }

    private void KaraokeTimer_Tick(object? sender, EventArgs e) => UpdateKaraokeProgress();

    private void UpdateKaraokeProgress()
    {
        var timing = _options.KaraokeEnabled && IsVisible && !string.IsNullOrWhiteSpace(_state.CurrentLyric)
            ? _state.Timing : null;
        var progress = timing?.ProgressAt(Stopwatch.GetElapsedTime(timing.ObservedTimestamp).TotalSeconds, _state.IsPlaying) ?? 0;
        var highlight = (Color)ColorConverter.ConvertFromString(_options.HighlightColor);
        PrimaryText.SetKaraokeProgress(_currentInSecondary ? 0 : progress, highlight);
        SecondaryText.SetKaraokeProgress(_currentInSecondary ? progress : 0, highlight);
        if (timing is not null && _state.IsPlaying && progress < 1)
            _karaokeTimer.Start();
        else
            _karaokeTimer.Stop();
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
        if (message == 0x02E0 || message == 0x001A) // DPI or work-area change
        {
            Dispatcher.BeginInvoke(PlaceWindow);
        }
        return 0;
    }

    private void PlaceWindow()
    {
        if (_placing || _resizing || _moving || Handle == 0) return;
        _placing = true;
        try
        {
            var monitor = FormsScreen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _options.Position?.Monitor)
                ?? FormsScreen.PrimaryScreen ?? FormsScreen.AllScreens[0];
            var work = monitor.WorkingArea;
            var scale = ResolveScale(monitor);
            var area = new PixelRect(work.Left, work.Top, work.Right, work.Bottom);
            var position = monitor.DeviceName == _options.Position?.Monitor ? _options.Position : null;
            MinWidth = 0;
            MaxWidth = Math.Min(DesktopLyricsPlacement.MaximumWidth, work.Width / scale);
            MinWidth = Math.Min(DesktopLyricsPlacement.MinimumWidth, MaxWidth);
            MinHeight = 0;
            MaxHeight = Math.Min(DesktopLyricsPlacement.MaximumHeight, work.Height / scale);
            Width = DesktopLyricsPlacement.Calculate(area, scale, Math.Max(1, ActualHeight), position).Width / scale;
            MinHeight = Math.Min(MinimumContentHeight(), MaxHeight);
            if (position?.Height is { } height)
            {
                SizeToContent = SizeToContent.Manual;
                Height = DesktopLyricsPlacement.ConstrainSize(Width, height, work.Width / scale, work.Height / scale, MinHeight).Height;
            }
            else
            {
                SizeToContent = SizeToContent.Height;
                Height = double.NaN;
            }
            UpdateLayout();
            UpdateLineSpacing();
            UpdateLayout();
            var rect = DesktopLyricsPlacement.Calculate(area, scale, Math.Max(1, ActualHeight), position);
            NativeMethods.SetWindowPos(Handle, new nint(-1), rect.Left, rect.Top, rect.Width, rect.Height,
                NativeMethods.SwpNoActivate);
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

    private void SavePosition()
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect)) return;
        var screen = FormsScreen.FromHandle(Handle);
        var work = screen.WorkingArea;
        var position = DesktopLyricsPlacement.Capture(screen.DeviceName,
            new(work.Left, work.Top, work.Right, work.Bottom), rect.ToPixelRect(), ResolveScale(screen),
            saveHeight: SizeToContent == SizeToContent.Manual);
        _options = _options with { Position = position };
        PlaceWindow();
        PositionChanged?.Invoke(position);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_options.Locked || e.OriginalSource is not DependencyObject target) return;
        for (var parent = target; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ButtonBase or Thumb) return;
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

    private void WidthHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeWindow(e.HorizontalChange, 0);

    private void HeightHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeWindow(0, e.VerticalChange);

    private void CornerHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeWindow(e.HorizontalChange, e.VerticalChange);

    private double MinimumContentHeight()
    {
        LyricsContent.Measure(new Size(Math.Max(1, Width - 24), double.PositiveInfinity));
        return Math.Max(DesktopLyricsPlacement.MinimumHeight, LyricsContent.DesiredSize.Height + 40 - _extraLineGap);
    }

    private void UpdateLineSpacing()
    {
        var extra = SizeToContent == SizeToContent.Manual && SecondaryText.Visibility == Visibility.Visible
            ? Math.Max(0, ActualHeight - MinimumContentHeight()) * 0.2 : 0;
        if (Math.Abs(extra - _extraLineGap) < 0.1) return;
        _extraLineGap = extra;
        var baseGap = _options.Layout == DesktopLyricsLayout.Horizontal ? 12 : 4;
        SecondaryText.Margin = new Thickness(4, baseGap + extra, 4, 8);
    }

    private void ResizeWindow(double widthChange, double heightChange)
    {
        if (_options.Locked) return;
        _resizing = true;
        var screen = FormsScreen.FromHandle(Handle);
        var work = screen.WorkingArea;
        var scale = ResolveScale(screen);
        Width = DesktopLyricsPlacement.ConstrainSize(Width + widthChange, ActualHeight, work.Width / scale, work.Height / scale).Width;
        MinHeight = Math.Min(MinimumContentHeight(), MaxHeight);
        if (heightChange != 0 || SizeToContent == SizeToContent.Manual)
        {
            var height = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            Height = DesktopLyricsPlacement.ConstrainSize(Width, height + heightChange, work.Width / scale, work.Height / scale, MinHeight).Height;
        }
        UpdateLayout();
        UpdateLineSpacing();
        UpdateLayout();
        if (NativeMethods.GetWindowRect(Handle, out var rect))
        {
            NativeMethods.SetWindowPos(Handle, new nint(-1),
                Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - rect.ToPixelRect().Width)),
                Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.ToPixelRect().Height)),
                0, 0, NativeMethods.SwpNoActivate | NativeMethods.SwpNoSize);
        }
    }

    private void WidthHandle_DragCompleted(object sender, DragCompletedEventArgs e)
        => ResizeHandle_DragCompleted(sender, e);

    private void ResizeHandle_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false;
        SavePosition();
    }

    private void UpdateHover()
    {
        var hover = !_options.Locked && Surface.IsMouseOver;
        Toolbar.Visibility = hover ? Visibility.Visible : Visibility.Hidden;
        var alpha = _options.Locked ? (byte)0 : (byte)Math.Round(255 * Math.Max(_options.BackgroundOpacity, hover ? 0.18 : 0));
        Backdrop.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        Surface.Background = _options.Locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

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
        else _unlockWindow?.Hide();
    }

    private void UpdateLockedHover(PixelPoint point)
    {
        if (!_options.Locked || !IsVisible || !NativeMethods.GetWindowRect(Handle, out var rect) ||
            point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
        {
            _unlockWindow?.Hide();
            return;
        }
        if (_unlockWindow is null)
        {
            var unlock = new DesktopLyricsUnlockWindow { Owner = this };
            unlock.UnlockRequested += UnlockWindow_UnlockRequested;
            unlock.Closed += (_, _) => { if (ReferenceEquals(_unlockWindow, unlock)) _unlockWindow = null; };
            _unlockWindow = unlock;
        }
        _unlockWindow.ShowAt(Handle, rect);
    }

    private void UnlockWindow_UnlockRequested() => CommandRequested?.Invoke(DesktopLyricsCommand.ToggleLocked);

    private void CloseUnlockWindow()
    {
        if (_unlockWindow is not { } unlock) return;
        _unlockWindow = null;
        unlock.UnlockRequested -= UnlockWindow_UnlockRequested;
        unlock.Close();
    }

    private void Surface_MouseEnter(object sender, MouseEventArgs e) => UpdateHover();
    private void Surface_MouseLeave(object sender, MouseEventArgs e) => UpdateHover();
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(PlaceWindow);
    private void LockButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.ToggleLocked);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.OpenSettings);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(DesktopLyricsCommand.ToggleEnabled);
}
