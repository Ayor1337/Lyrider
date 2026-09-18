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

    internal TrayMenuWindow(Action openWindow, Action exitApplication)
    {
        _openWindow = openWindow;
        _exitApplication = exitApplication;
        InitializeComponent();
    }

    internal void ApplyTheme(bool isLightTheme)
    {
        SetBrush("TrayMenuBackgroundBrush", isLightTheme ? "#FCFCFC" : "#F52B2B2B");
        SetBrush("TrayMenuBorderBrush", isLightTheme ? "#24000000" : "#33FFFFFF");
        SetBrush("TrayMenuForegroundBrush", isLightTheme ? "#1A1A1A" : "#FFFFFF");
        SetBrush("TrayMenuHoverBrush", isLightTheme ? "#0F000000" : "#12FFFFFF");
        SetBrush("TrayMenuPressedBrush", isLightTheme ? "#18000000" : "#1FFFFFFF");
        SetBrush("TrayMenuSeparatorBrush", isLightTheme ? "#17000000" : "#18FFFFFF");
        SetBrush("TrayMenuFocusBrush", isLightTheme ? "#990078D4" : "#99A7C7FF");
    }

    internal void ShowAt(DrawingPoint cursorPosition)
    {
        Show();

        var windowHandle = new WindowInteropHelper(this).Handle;
        var scale = Math.Max(1, NativeMethods.GetDpiForWindow(windowHandle)) / 96.0;
        var workingArea = FormsScreen.FromPoint(cursorPosition).WorkingArea;
        var cursorX = cursorPosition.X / scale;
        var cursorY = cursorPosition.Y / scale;
        var workingLeft = workingArea.Left / scale;
        var workingTop = workingArea.Top / scale;
        var workingRight = workingArea.Right / scale;
        var workingBottom = workingArea.Bottom / scale;

        Left = Math.Clamp(cursorX, workingLeft, Math.Max(workingLeft, workingRight - ActualWidth));
        Top = cursorY + ActualHeight <= workingBottom
            ? cursorY
            : Math.Max(workingTop, workingBottom - ActualHeight);
        Activate();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => _openWindow();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => _exitApplication();

    private void MenuWindow_Deactivated(object? sender, EventArgs e) => Close();

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
