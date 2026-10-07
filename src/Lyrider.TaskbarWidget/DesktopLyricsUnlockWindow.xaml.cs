using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Lyrider.TaskbarWidget;

public partial class DesktopLyricsUnlockWindow : Window
{
    private HwndSource? _source;
    private DispatcherOperation? _revealOperation;
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
        if (Opacity == 0)
        {
            _revealOperation?.Abort();
            _revealOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _revealOperation = null;
                if (IsVisible && Owner is { IsVisible: true, Opacity: > 0 })
                {
                    Opacity = 1;
                }
            }));
        }
    }

    private static nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 3; // MA_NOACTIVATE
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => UnlockRequested?.Invoke();
}
