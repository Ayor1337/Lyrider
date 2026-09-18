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
        SetBrush("TrayMenuBackgroundBrush", isLightTheme ? "#F9F9F9" : "#2C2C2C");
        SetBrush("TrayMenuBorderBrush", isLightTheme ? "#D2D2D2" : "#4B4B4B");
        SetBrush("TrayMenuForegroundBrush", isLightTheme ? "#1A1A1A" : "#FFFFFF");
        SetBrush("TrayMenuHoverBrush", isLightTheme ? "#E8E8E8" : "#3E3E3E");
        SetBrush("TrayMenuPressedBrush", isLightTheme ? "#DEDEDE" : "#494949");
        SetBrush("TrayMenuSeparatorBrush", isLightTheme ? "#DADADA" : "#484848");
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
