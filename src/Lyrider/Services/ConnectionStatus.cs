namespace Lyrider.Services;

/// <summary>
/// Smooths the one-second Cider poll so a single timeout does not flash the player into its disconnected state.
/// </summary>
internal sealed class ConnectionStatusTracker
{
    public const int FailureThreshold = 3;

    private int _consecutiveFailures;

    /// <summary>The state shown to the user, or null while the first connection is still pending.</summary>
    public CiderConnectionState? State { get; private set; }

    public string Message { get; private set; } = string.Empty;

    /// <summary>Returns false while a transient failure is absorbed and the last shown state still stands.</summary>
    public bool Report(CiderConnectionState state, string message)
    {
        if (state == CiderConnectionState.Connected)
        {
            _consecutiveFailures = 0;
        }
        else if (state != CiderConnectionState.Unauthorized &&
            State is null or CiderConnectionState.Connected &&
            ++_consecutiveFailures < FailureThreshold)
        {
            return false;
        }

        State = state;
        Message = message;
        return true;
    }

    public void Reset()
    {
        _consecutiveFailures = 0;
        State = null;
        Message = string.Empty;
    }
}

internal static class ConnectionStatusPresentation
{
    public static ConnectionDisplay For(CiderConnectionState? state) =>
        state switch
        {
            null => new ConnectionDisplay("ConnectingBrush", null, ConnectionAction.None),
            CiderConnectionState.Connected => new ConnectionDisplay("ConnectedBrush", null, ConnectionAction.None),
            CiderConnectionState.Unauthorized => new ConnectionDisplay(
                "WarningBrush",
                AppText.Get("需要有效的 Token", "A valid token is required"),
                ConnectionAction.Configure),
            CiderConnectionState.Offline => new ConnectionDisplay(
                "DisconnectedBrush",
                AppText.Get("未连接到 Cider", "Not connected to Cider"),
                ConnectionAction.Retry),
            _ => new ConnectionDisplay(
                "DisconnectedBrush",
                AppText.Get("Cider 连接出错", "Cider connection error"),
                ConnectionAction.Retry)
        };

    public static string ActionLabel(ConnectionAction action) =>
        action == ConnectionAction.Configure
            ? AppText.Get("配置连接", "Configure connection")
            : AppText.Get("重试", "Retry");
}

internal enum ConnectionAction
{
    None,
    Retry,
    Configure
}

/// <param name="IssueTitle">Replaces "Not Playing" on the player while the connection is unusable.</param>
internal readonly record struct ConnectionDisplay(
    string BrushKey,
    string? IssueTitle,
    ConnectionAction Action);
