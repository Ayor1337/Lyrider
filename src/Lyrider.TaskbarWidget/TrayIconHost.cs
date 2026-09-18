using System.Drawing;
using System.Windows.Forms;

namespace Lyrider.TaskbarWidget;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _showMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;
    private readonly Action _showWindow;
    private readonly Action _exitApplication;
    private bool _isDisposed;

    public TrayIconHost(string iconPath, Action showWindow, Action exitApplication)
    {
        _showWindow = showWindow;
        _exitApplication = exitApplication;

        _showMenuItem = new ToolStripMenuItem("显示 Lyrider");
        _showMenuItem.Click += ShowMenuItem_Click;
        _exitMenuItem = new ToolStripMenuItem("退出 Lyrider");
        _exitMenuItem.Click += ExitMenuItem_Click;

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.AddRange([_showMenuItem, new ToolStripSeparator(), _exitMenuItem]);

        _icon = new Icon(iconPath);
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _icon,
            Text = "Lyrider",
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) => _showWindow();

    private void ShowMenuItem_Click(object? sender, EventArgs e) => _showWindow();

    private void ExitMenuItem_Click(object? sender, EventArgs e) => _exitApplication();

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _showMenuItem.Click -= ShowMenuItem_Click;
        _exitMenuItem.Click -= ExitMenuItem_Click;
        _notifyIcon.Dispose();
        _icon.Dispose();
        _contextMenu.Dispose();
    }
}
