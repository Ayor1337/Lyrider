using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DrawingPoint = System.Drawing.Point;
using FormsScreen = System.Windows.Forms.Screen;
using MediaColor = System.Windows.Media.Color;

namespace Lyrider.TaskbarWidget;

public partial class TrayMenuWindow : Window
{
    private readonly Action _openWindow;
    private readonly Action _exitApplication;
    private bool _isLightTheme = true;
    private bool _isClosing;

    private nint Handle => new WindowInteropHelper(this).Handle;

    public event Action<DesktopLyricsCommand>? DesktopLyricsCommandRequested;

    internal void SetDesktopLyricsState(bool enabled, bool locked)
    {
        DesktopLyricsToggleButton.Content = WidgetText.Get("显示桌面歌词", "Show desktop lyrics");
        DesktopLyricsToggleButton.IsChecked = enabled;
        DesktopLyricsLockButton.Content = WidgetText.Get("锁定桌面歌词", "Lock desktop lyrics");
        DesktopLyricsLockButton.IsChecked = locked;
        DesktopLyricsLockButton.IsEnabled = enabled;
        DesktopLyricsSettingsButton.Content = WidgetText.Get("桌面歌词设置", "Desktop lyrics settings");
        DesktopLyricsResetButton.Content = WidgetText.Get("重置歌词位置", "Reset lyrics position");
        foreach (var button in new ButtonBase[]
        {
            DesktopLyricsToggleButton, DesktopLyricsLockButton, DesktopLyricsSettingsButton, DesktopLyricsResetButton
        })
        {
            AutomationProperties.SetName(button, button.Content.ToString()!);
        }
    }

    private void DesktopLyricsToggleButton_Click(object sender, RoutedEventArgs e) => DesktopLyricsCommandRequested?.Invoke(DesktopLyricsCommand.ToggleEnabled);
    private void DesktopLyricsLockButton_Click(object sender, RoutedEventArgs e) => DesktopLyricsCommandRequested?.Invoke(DesktopLyricsCommand.ToggleLocked);
    private void DesktopLyricsSettingsButton_Click(object sender, RoutedEventArgs e) => DesktopLyricsCommandRequested?.Invoke(DesktopLyricsCommand.OpenSettings);
    private void DesktopLyricsResetButton_Click(object sender, RoutedEventArgs e) => DesktopLyricsCommandRequested?.Invoke(DesktopLyricsCommand.ResetPosition);

    internal TrayMenuWindow(Action openWindow, Action exitApplication)
    {
        _openWindow = openWindow;
        _exitApplication = exitApplication;
        InitializeComponent();
        OpenButton.Content = WidgetText.Get("打开 Lyrider", "Open Lyrider");
        AutomationProperties.SetName(OpenButton, OpenButton.Content.ToString()!);
        ExitButton.Content = WidgetText.Get("退出 Lyrider", "Exit Lyrider");
        AutomationProperties.SetName(ExitButton, ExitButton.Content.ToString()!);
        SetDesktopLyricsState(false, false);
        SourceInitialized += TrayMenuWindow_SourceInitialized;
        Closing += TrayMenuWindow_Closing;
    }

    internal void ApplyTheme(bool isLightTheme)
    {
        _isLightTheme = isLightTheme;
        if (Handle != nint.Zero)
        {
            var darkMode = isLightTheme ? 0 : 1;
            NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaUseImmersiveDarkMode,
                ref darkMode, sizeof(int));
        }

        SetBrush("TrayMenuBackgroundBrush", isLightTheme ? "#F9F9F9" : "#202020");
        SetBrush("TrayMenuForegroundBrush", isLightTheme ? "#1A1A1A" : "#FFFFFF");
        SetBrush("TrayMenuHoverBrush", isLightTheme ? "#E9E9E9" : "#353535");
        SetBrush("TrayMenuPressedBrush", isLightTheme ? "#DDDDDD" : "#404040");
        SetBrush("TrayMenuSeparatorBrush", isLightTheme ? "#28000000" : "#38FFFFFF");
        SetBrush("TrayMenuFocusBrush", isLightTheme ? "#990078D4" : "#99A7C7FF");
    }

    internal void ShowAt(DrawingPoint cursorPosition)
    {
        Show();
        UpdateLayout();

        var cursor = new NativePoint { X = cursorPosition.X, Y = cursorPosition.Y };
        var workingArea = FormsScreen.FromPoint(cursorPosition).WorkingArea;
        var position = TrayMenuPlacement.Calculate(
            ResolveDpi(Handle, cursor),
            new PixelPoint(cursorPosition.X, cursorPosition.Y),
            new PixelRect(workingArea.Left, workingArea.Top, workingArea.Right, workingArea.Bottom),
            ActualWidth,
            ActualHeight);

        Left = position.X;
        Top = position.Y;
        Activate();
    }

    /// <summary>
    /// 取光标所在显示器的 DPI。窗口此刻刚创建、仍在主显示器上，
    /// 用 <c>GetDpiForWindow</c> 会在副屏上取到错误的缩放比。
    /// </summary>
    private static int ResolveDpi(nint windowHandle, NativePoint cursor)
    {
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        if (monitor != nint.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return (int)dpiX;
        }

        var windowDpi = NativeMethods.GetDpiForWindow(windowHandle);
        return windowDpi > 0 ? (int)windowDpi : TrayMenuPlacement.ReferenceDpi;
    }

    private void TrayMenuWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var cornerPreference = NativeMethods.DwmcpRound;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference, sizeof(int));
        var borderColor = NativeMethods.DwmwaColorNone;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaBorderColor,
            ref borderColor, sizeof(int));
        ApplyTheme(_isLightTheme);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => _openWindow();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => _exitApplication();

    private void TrayMenuWindow_Closing(object? sender, CancelEventArgs e) => _isClosing = true;

    private void MenuWindow_Deactivated(object? sender, EventArgs e)
    {
        // 关闭流程中仍会收到 WM_ACTIVATE，此时再调 Close 会抛 InvalidOperationException，
        // 例如点“退出 Lyrider”后菜单已由 TrayIconHost 关闭、应用随后退出。
        if (!_isClosing)
        {
            Close();
        }
    }

    private void MenuWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void SetBrush(string key, string color) =>
        Resources[key] = new SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
