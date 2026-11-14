using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lyrider.TaskbarWidget;

public partial class DesktopLyricsUnlockWindow : Window
{
    private HwndSource? _source;
    private DispatcherOperation? _revealOperation;
    // 目标是否为显示状态；淡出过程中窗口仍可见，但目标已是隐藏。
    private bool _shown;
    // 是否已开始淡入。歌词窗口尚未显示时淡入会推迟，之后的 ShowAt 需要重新安排。
    private bool _fadeStarted;
    private static readonly IEasingFunction ShowEasing = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction HideEasing = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    private nint Handle => new WindowInteropHelper(this).Handle;

    public event Action? UnlockRequested;

    public DesktopLyricsUnlockWindow()
    {
        InitializeComponent();
        UnlockButton.ToolTip = WidgetText.Get("解锁桌面歌词", "Unlock desktop lyrics");
        AutomationProperties.SetName(UnlockButton, (string)UnlockButton.ToolTip);
        SourceInitialized += (_, _) =>
        {
            var style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GwlExStyle).ToInt64();
            style = (style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)
                & ~(NativeMethods.WsExAppWindow | NativeMethods.WsExTransparent);
            NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlExStyle, new nint(style));
            _source = HwndSource.FromHwnd(Handle);
            _source?.AddHook(WindowHook);
        };
        // 无论由谁隐藏窗口，都回到静止的隐藏状态，下次显示重新淡入。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _shown = false;
            _fadeStarted = false;
            _revealOperation?.Abort();
            _revealOperation = null;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
        };
        Closed += (_, _) =>
        {
            _revealOperation?.Abort();
            _source?.RemoveHook(WindowHook);
        };
    }

    internal void ShowAt(nint lyricsHandle, NativeRect lyricsRect, Rect toolbarButtonBounds)
    {
        Width = toolbarButtonBounds.Width;
        Height = toolbarButtonBounds.Height;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        var scale = Math.Max(96, NativeMethods.GetDpiForWindow(lyricsHandle)) / 96.0;
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(Height * scale);
        NativeMethods.SetWindowPos(Handle, new nint(-1),
            lyricsRect.Left + (lyricsRect.Right - lyricsRect.Left - width) / 2,
            lyricsRect.Top + (int)Math.Round(toolbarButtonBounds.Top * scale), width, height, NativeMethods.SwpNoActivate);
        UpdateLayout();
        UnlockButton.IsHitTestVisible = true;
        if (_shown && (_fadeStarted || _revealOperation is not null)) return;
        _shown = true;
        if (Opacity > 0)
        {
            // 正在淡出时从当前不透明度接着淡入。
            _fadeStarted = true;
            FadeTo(1);
            return;
        }
        _revealOperation?.Abort();
        // 等布局与渲染完成后再淡入首帧，避免先显示未就位的图标。
        _revealOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _revealOperation = null;
            if (!_shown || !IsVisible || Owner is not { IsVisible: true, Opacity: > 0 }) return;
            _fadeStarted = true;
            FadeTo(1);
        }));
    }

    /// <summary>鼠标离开歌词时淡出，结束后隐藏；淡出期间不再响应点击。</summary>
    internal void FadeOut()
    {
        if (!IsVisible || !_shown) return;
        _shown = false;
        _fadeStarted = false;
        _revealOperation?.Abort();
        _revealOperation = null;
        UnlockButton.IsHitTestVisible = false;
        if (Opacity <= 0)
        {
            Hide();
            return;
        }
        FadeTo(0);
    }

    // 与桌面歌词窗口的悬停动画同节奏：出现 160ms 减速，消失 200ms 加速。
    private void FadeTo(double opacity)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = opacity;
            if (opacity == 0) Hide();
            return;
        }
        var showing = opacity > 0;
        var animation = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(showing ? 160 : 200))
        {
            EasingFunction = showing ? ShowEasing : HideEasing
        };
        if (!showing)
        {
            animation.Completed += (_, _) =>
            {
                if (!_shown && IsVisible) Hide();
            };
        }
        BeginAnimation(OpacityProperty, animation);
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    private static nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 3; // MA_NOACTIVATE
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => UnlockRequested?.Invoke();
}
