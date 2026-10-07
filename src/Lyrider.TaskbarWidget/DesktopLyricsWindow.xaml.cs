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
    private bool _resizing;
    private bool _moving;
    private OutlinedLyricText PrimaryText => LyricsContent.PrimaryText;
    private OutlinedLyricText SecondaryText => LyricsContent.SecondaryText;
    private double _extraLineGap;
    private bool _currentInSecondary;
    private HwndSource? _source;
    private readonly DispatcherTimer _karaokeTimer;
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
        Surface.ContextMenu = _quickMenu;
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
        PreviousButton.ToolTip = WidgetText.Get("上一首", "Previous");
        NextButton.ToolTip = WidgetText.Get("下一首", "Next");
        AutomationProperties.SetName(PreviousButton, (string)PreviousButton.ToolTip);
        AutomationProperties.SetName(NextButton, (string)NextButton.ToolTip);
        LockButton.ToolTip = WidgetText.Get("锁定", "Lock");
        CloseButton.ToolTip = WidgetText.Get("关闭歌词", "Close lyrics");
        AutomationProperties.SetName(LockButton, (string)LockButton.ToolTip);
        AutomationProperties.SetName(CloseButton, (string)CloseButton.ToolTip);
        SourceInitialized += Window_SourceInitialized;
        SizeChanged += (_, _) => { if (!_placing && !_resizing && Handle != 0) PlaceWindow(); };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _quickMenu.IsOpen = false;
            _revealOperation?.Abort();
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
        PlayPauseIcon.Text = TaskbarPresentation.GetPlayPauseGlyph(_state.IsPlaying);
        PlayPauseButton.ToolTip = _state.IsPlaying ? WidgetText.Get("暂停", "Pause") : WidgetText.Get("播放", "Play");
        AutomationProperties.SetName(PlayPauseButton, (string)PlayPauseButton.ToolTip);
        var text = LyricsContent.Apply(_state, _options);
        _currentInSecondary = text.CurrentInSecondary;
        _extraLineGap = 0;
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

    private void KaraokeTimer_Tick(object? sender, EventArgs e) => UpdateKaraokeProgress();

    private void UpdateKaraokeProgress()
    {
        var timing = _options.KaraokeEnabled && IsVisible && !DesktopLyricsPresentation.UsesTranslation(_state, _options) &&
            !string.IsNullOrWhiteSpace(_state.CurrentLyric)
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

    private void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_options.Locked) return;
        _quickMenu.PlacementTarget = Surface;
        _quickMenu.Placement = PlacementMode.MousePoint;
        _quickMenu.IsOpen = true;
        e.Handled = true;
    }

    private void WidthHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeWindow(e.HorizontalChange, 0);

    private void HeightHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeWindow(0, e.VerticalChange);

    private void CornerHandle_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeWindow(e.HorizontalChange, e.VerticalChange);

    private double MinimumContentHeight(double? width = null)
    {
        LyricsContent.Measure(new Size(Math.Max(1, (width ?? Width) - 24), double.PositiveInfinity));
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

    private void ResizeWindow(double widthChange, double heightChange)
    {
        if (_options.Locked) return;
        _resizing = true;
        var screen = FormsScreen.FromHandle(Handle);
        var work = screen.WorkingArea;
        var scale = ResolveScale(screen);
        Width = DesktopLyricsPlacement.ConstrainSize(Width + widthChange, ActualHeight, work.Width / scale, work.Height / scale).Width;
        if (_options.TextDirection == DesktopLyricsTextDirection.Vertical && heightChange != 0)
        {
            PrimaryText.MaxHeight = SecondaryText.MaxHeight = Math.Max(1, ActualHeight + heightChange - DesktopLyricsContent.WindowVerticalPadding - LyricsContent.Margin.Top - LyricsContent.Margin.Bottom);
        }
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
        var hover = !_options.Locked && (Surface.IsMouseOver || _quickMenu.IsOpen);
        Toolbar.Visibility = hover ? Visibility.Visible : Visibility.Hidden;
        CornerHandle.Opacity = hover ? 1 : 0;
        var alpha = _options.Locked ? (byte)0 : (byte)Math.Round(255 * Math.Max(_options.BackgroundOpacity, hover ? 0.45 : 0));
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
