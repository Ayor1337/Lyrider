using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingPoint = System.Drawing.Point;
using FormsScreen = System.Windows.Forms.Screen;
using MediaColor = System.Windows.Media.Color;

namespace Lyrider.TaskbarWidget;

public partial class TrayMenuWindow : Window
{
    private readonly Action _openWindow;
    private readonly Action _exitApplication;
    private bool _isLightTheme = true;
    private bool _useAcrylic;
    private bool _isClosing;

    private nint Handle => new WindowInteropHelper(this).Handle;

    internal TrayMenuWindow(Action openWindow, Action exitApplication)
    {
        _openWindow = openWindow;
        _exitApplication = exitApplication;
        InitializeComponent();
        SourceInitialized += TrayMenuWindow_SourceInitialized;
        Closing += TrayMenuWindow_Closing;
    }

    internal void ApplyTheme(bool isLightTheme)
    {
        _isLightTheme = isLightTheme;
        if (_useAcrylic)
        {
            // 深浅色会影响 DWM 材质的着色调，切换主题时需要重新下发。
            WindowMaterial.TryApplyAcrylic(Handle, isLightTheme);
        }

        // 亚克力可用时留出透明度让材质透出来，否则退回不透明纯色。
        SetBrush("TrayMenuBackgroundBrush", isLightTheme
            ? (_useAcrylic ? "#CCF9F9F9" : "#F9F9F9")
            : (_useAcrylic ? "#CC2B2B2B" : "#2B2B2B"));
        SetBrush("TrayMenuBorderBrush", isLightTheme ? "#18000000" : "#1FFFFFFF");
        SetBrush("TrayMenuForegroundBrush", isLightTheme ? "#1A1A1A" : "#FFFFFF");
        SetBrush("TrayMenuHoverBrush", isLightTheme ? "#0A000000" : "#0FFFFFFF");
        SetBrush("TrayMenuPressedBrush", isLightTheme ? "#14000000" : "#17FFFFFF");
        SetBrush("TrayMenuSeparatorBrush", isLightTheme ? "#14000000" : "#1FFFFFFF");
        SetBrush("TrayMenuFocusBrush", isLightTheme ? "#990078D4" : "#99A7C7FF");
    }

    internal void ShowAt(DrawingPoint cursorPosition)
    {
        Show();
        UpdateLayout();

        // 材质在 SourceInitialized 里才判定，此处 Handle 必定有效。
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
        _useAcrylic = WindowMaterial.TryApplyAcrylic(Handle, _isLightTheme);

        // 构造时用的还是回退配色，材质判定后需要重刷一次。
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
