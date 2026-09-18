using System.Windows;
using System.Windows.Threading;

namespace Lyrider.TaskbarWidget;

public sealed class TaskbarWidgetHost : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _threadReady = new();

    private System.Windows.Application? _application;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _refreshCancellation;
    private TaskbarWidgetWindow? _window;
    private Thread? _thread;
    private TaskbarPlaybackState _latestState = TaskbarPlaybackState.Unavailable;
    private bool _enabled;
    private bool _disposed;
    private bool _refreshInProgress;

    public event Action<TaskbarPlaybackCommand>? CommandRequested;

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsSupported)
            {
                return;
            }

            _enabled = true;
            if (_thread is null)
            {
                _thread = new Thread(ThreadMain)
                {
                    IsBackground = true,
                    Name = "Lyrider taskbar widget"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        _threadReady.Wait(TimeSpan.FromSeconds(3));
        _dispatcher?.BeginInvoke(StartOnDispatcher);
    }

    public void Stop()
    {
        _refreshCancellation?.Cancel();
        lock (_gate)
        {
            _enabled = false;
            _latestState = TaskbarPlaybackState.Unavailable;
        }

        _dispatcher?.BeginInvoke(StopOnDispatcher);
    }

    public void Update(TaskbarPlaybackState state)
    {
        lock (_gate)
        {
            _latestState = state;
            if (!_enabled || _disposed)
            {
                return;
            }
        }

        _dispatcher?.BeginInvoke(() => _window?.SetPlaybackState(state));
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            thread = _thread;
        }

        if (thread is not null)
        {
            _threadReady.Wait(TimeSpan.FromSeconds(3));
        }

        _dispatcher?.BeginInvoke(() =>
        {
            StopOnDispatcher();
            _application?.Shutdown();
        });
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(3));
        }

        _threadReady.Dispose();
    }

    private void ThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            _refreshTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(1500),
                DispatcherPriority.Background,
                async (_, _) => await EnsureAndRefreshWindowAsync(GetRefreshToken()),
                _dispatcher);
            _threadReady.Set();
            if (IsDisposed())
            {
                return;
            }

            if (IsEnabled())
            {
                StartOnDispatcher();
            }

            _application.Run();
            _refreshTimer.Stop();
            CloseWindow();
        }
        catch (Exception)
        {
            _threadReady.Set();
        }
    }

    private void StartOnDispatcher()
    {
        if (!IsEnabled())
        {
            return;
        }

        _refreshTimer?.Start();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        _ = EnsureAndRefreshWindowAsync(_refreshCancellation.Token);
    }

    private void StopOnDispatcher()
    {
        _refreshTimer?.Stop();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        CloseWindow();
    }

    private async Task EnsureAndRefreshWindowAsync(CancellationToken cancellationToken)
    {
        if (_refreshInProgress || !IsEnabled())
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            if (_window is null || _window.Handle == nint.Zero || !NativeMethods.IsWindow(_window.Handle))
            {
                CloseWindow();
                _window = new TaskbarWidgetWindow();
                _window.CommandRequested += Window_CommandRequested;
                _window.Closed += Window_Closed;
                _window.Show();
            }

            var window = _window;
            window.SetPlaybackState(GetLatestState());
            await window.RefreshHostAsync(cancellationToken);
        }
        catch (Exception)
        {
            CloseWindow();
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void CloseWindow()
    {
        if (_window is null)
        {
            return;
        }

        var window = _window;
        _window = null;
        window.CommandRequested -= Window_CommandRequested;
        window.Closed -= Window_Closed;
        try
        {
            window.Close();
        }
        catch (InvalidOperationException)
        {
            // Explorer may have already destroyed the child HWND.
        }
    }

    private bool IsEnabled()
    {
        lock (_gate)
        {
            return _enabled && !_disposed;
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private TaskbarPlaybackState GetLatestState()
    {
        lock (_gate)
        {
            return _latestState;
        }
    }

    private CancellationToken GetRefreshToken() =>
        _refreshCancellation?.Token ?? new CancellationToken(canceled: true);

    private void Window_CommandRequested(TaskbarPlaybackCommand command)
    {
        try
        {
            CommandRequested?.Invoke(command);
        }
        catch (Exception)
        {
            // The taskbar UI must not terminate the WPF dispatcher when a consumer is shutting down.
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _window))
        {
            _window = null;
        }
    }
}
