using System.Drawing;
using System.Windows.Forms;

namespace Lyrider.TaskbarWidget;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Action _showWindow;
    private readonly Action _exitApplication;
    private TrayMenuWindow? _menuWindow;
    private bool _isLightTheme = true;
    private bool _isDisposed;

    public TrayIconHost(string iconPath, Action showWindow, Action exitApplication)
    {
        _showWindow = showWindow;
        _exitApplication = exitApplication;
        _icon = new Icon(iconPath);
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Lyrider",
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
    }

    public void SetLightTheme(bool isLightTheme)
    {
        _isLightTheme = isLightTheme;
        _menuWindow?.ApplyTheme(isLightTheme);
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) => _showWindow();

    private void NotifyIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        _menuWindow?.Close();
        _menuWindow = new TrayMenuWindow(OpenWindow, ExitApplication);
        _menuWindow.ApplyTheme(_isLightTheme);
        _menuWindow.Closed += MenuWindow_Closed;
        _menuWindow.ShowAt(Cursor.Position);
    }

    private void OpenWindow()
    {
        _menuWindow?.Close();
        _showWindow();
    }

    private void ExitApplication()
    {
        _menuWindow?.Close();
        _exitApplication();
    }

    private void MenuWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is TrayMenuWindow window)
        {
            window.Closed -= MenuWindow_Closed;
        }

        _menuWindow = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _notifyIcon.MouseUp -= NotifyIcon_MouseUp;
        _menuWindow?.Close();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
