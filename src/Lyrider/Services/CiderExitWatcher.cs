using System.Diagnostics;

namespace Lyrider.Services;

/// <summary>
/// Decides when Cider has quit: the watch arms once a Cider process has been seen, so starting
/// Lyrider before Cider does not end it, and fires when that process is gone.
/// </summary>
internal sealed class CiderExitWatcher
{
    public const string ProcessName = "Cider";

    private readonly Func<bool> _isCiderRunning;
    private bool _hasSeenCider;

    public CiderExitWatcher()
        : this(IsCiderProcessRunning)
    {
    }

    public CiderExitWatcher(Func<bool> isCiderRunning)
    {
        _isCiderRunning = isCiderRunning;
    }

    /// <summary>Returns true once, when a Cider seen earlier is no longer running.</summary>
    public bool Poll()
    {
        if (_isCiderRunning())
        {
            _hasSeenCider = true;
            return false;
        }

        if (!_hasSeenCider)
        {
            return false;
        }

        _hasSeenCider = false;
        return true;
    }

    public void Reset() => _hasSeenCider = false;

    private static bool IsCiderProcessRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}
