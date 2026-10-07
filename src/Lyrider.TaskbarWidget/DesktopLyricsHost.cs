using System.Windows.Threading;

namespace Lyrider.TaskbarWidget;

public sealed class DesktopLyricsHost : IDisposable
{
    private readonly object _gate = new();
    private DesktopLyricsOptions _options = new();
    private DesktopLyricsState _state = DesktopLyricsState.Unavailable;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private DesktopLyricsWindow? _window;
    private bool _disposed;
    private bool _refreshQueued;

    public event Action<DesktopLyricsCommand>? CommandRequested;
    public event Action<DesktopLyricsPosition>? PositionChanged;
    public event Action? Failed;

    public void ApplyOptions(DesktopLyricsOptions options)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _options = options.Normalize();
            if (_options.Enabled && _thread is null)
            {
                _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Lyrider desktop lyrics" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            QueueRefresh();
        }
    }

    public void Update(DesktopLyricsState state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _state = state;
            QueueRefresh();
        }
    }

    public void ResetPosition()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _options = _options with { Position = null };
            QueueRefresh();
        }
    }

    private void QueueRefresh()
    {
        if (_dispatcher is null || _refreshQueued || _dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        _dispatcher.BeginInvoke(Refresh);
    }

    private void ThreadMain()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                _dispatcher = Dispatcher.CurrentDispatcher;
                QueueRefresh();
            }
            Dispatcher.Run();
        }
        catch (Exception)
        {
            Failed?.Invoke();
        }
        finally
        {
            CloseWindow();
            lock (_gate)
            {
                _dispatcher = null;
                _thread = null;
                _refreshQueued = false;
            }
        }
    }

    private void Refresh()
    {
        DesktopLyricsOptions options;
        DesktopLyricsState state;
        lock (_gate)
        {
            _refreshQueued = false;
            if (_disposed) return;
            options = _options;
            state = _state;
        }

        if (!options.Enabled)
        {
            CloseWindow();
            return;
        }

        if (_window is null)
        {
            _window = new DesktopLyricsWindow();
            _window.CommandRequested += Window_CommandRequested;
            _window.PositionChanged += Window_PositionChanged;
        }
        _window.ApplyOptions(options);
        _window.SetPlaybackState(state);
    }

    private void Window_CommandRequested(DesktopLyricsCommand command) => CommandRequested?.Invoke(command);

    private void Window_PositionChanged(DesktopLyricsPosition position)
    {
        lock (_gate)
        {
            _options = _options with { Position = position };
        }
        PositionChanged?.Invoke(position);
    }

    private void CloseWindow()
    {
        if (_window is null) return;
        var window = _window;
        _window = null;
        window.CommandRequested -= Window_CommandRequested;
        window.PositionChanged -= Window_PositionChanged;
        window.Close();
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
            if (_dispatcher is { HasShutdownStarted: false } dispatcher)
            {
                dispatcher.BeginInvoke(() =>
                {
                    CloseWindow();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                });
            }
        }
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }
    }
}
