using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace Lyrider.TaskbarWidget;

public partial class DesktopLyricsUnlockWindow : Window
{
    private HwndSource? _source;
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
        Closed += (_, _) => _source?.RemoveHook(WindowHook);
    }

    internal void ShowAt(nint lyricsHandle, NativeRect lyricsRect)
    {
        if (!IsVisible) Show();
        var scale = Math.Max(96, NativeMethods.GetDpiForWindow(lyricsHandle)) / 96.0;
        var size = (int)Math.Ceiling(Width * scale);
        NativeMethods.SetWindowPos(Handle, new nint(-1),
            lyricsRect.Left + (lyricsRect.Right - lyricsRect.Left - size) / 2,
            lyricsRect.Top + (int)Math.Round(6 * scale), size, size, NativeMethods.SwpNoActivate);
    }

    private static nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 3; // MA_NOACTIVATE
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => UnlockRequested?.Invoke();
}
