using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Lyrider.Services;

/// <summary>
/// A playback-time sample pushed by Cider. <see cref="SampledAtEpochMs"/> is Cider's wall
/// clock when <see cref="Position"/> was read, or 0 when the event did not carry it.
/// </summary>
public sealed record PlaybackTimeSample(
    double Position,
    double Duration,
    bool IsPlaying,
    long SampledAtEpochMs)
{
    private static readonly TimeSpan MaxTrustedAge = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long ago the sample was taken, given the current Unix time in milliseconds.
    /// Cider runs on the same machine, so its wall clock matches ours; an implausible age
    /// (missing timestamp, clock change) falls back to zero, i.e. "just received".
    /// </summary>
    public TimeSpan AgeAt(long nowEpochMs)
    {
        if (SampledAtEpochMs <= 0)
        {
            return TimeSpan.Zero;
        }

        var age = TimeSpan.FromMilliseconds(nowEpochMs - SampledAtEpochMs);
        return age < TimeSpan.Zero || age > MaxTrustedAge ? TimeSpan.Zero : age;
    }
}

/// <summary>
/// Minimal Socket.IO (Engine.IO v4, websocket transport) listener for Cider's
/// <c>playbackStatus.playbackTimeDidChange</c> events. The REST position is a snapshot that
/// lags by up to a second; these events arrive as the position changes.
/// </summary>
public sealed class CiderPlaybackSocket : IDisposable
{
    private const string TimeEventType = "playbackStatus.playbackTimeDidChange";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _cancellation = new();
    private bool _started;

    /// <summary>Raised on a background thread.</summary>
    public event Action<PlaybackTimeSample>? TimeChanged;

    public void Start(Func<Uri> baseAddress)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(() => RunAsync(baseAddress, _cancellation.Token));
    }

    // The source is only cancelled, not disposed: the receive loop may still observe it.
    public void Dispose() => _cancellation.Cancel();

    internal static Uri SocketUri(Uri baseAddress)
    {
        var builder = new UriBuilder(new Uri(baseAddress, "socket.io/"))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = "EIO=4&transport=websocket"
        };
        return builder.Uri;
    }

    internal static bool TryParseTimeEvent(string frame, out PlaybackTimeSample sample)
    {
        sample = null!;
        if (!frame.StartsWith("42[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(frame.AsMemory(2));
            var root = document.RootElement;
            if (root.GetArrayLength() < 2 ||
                root[0].ValueKind != JsonValueKind.String ||
                root[0].GetString() != "API:Playback" ||
                root[1].ValueKind != JsonValueKind.Object ||
                !root[1].TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != TimeEventType ||
                !root[1].TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !TryGetDouble(data, "currentPlaybackTime", out var position) ||
                !TryGetDouble(data, "currentPlaybackDuration", out var duration))
            {
                return false;
            }

            var isPlaying = data.TryGetProperty("isPlaying", out var playing) &&
                playing.ValueKind == JsonValueKind.True;
            var sampledAt = data.TryGetProperty("anchor", out var anchor) &&
                anchor.ValueKind == JsonValueKind.Object &&
                TryGetDouble(anchor, "timestamp", out var timestamp)
                    ? (long)timestamp
                    : 0;
            sample = new PlaybackTimeSample(position, duration, isPlaying, sampledAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private async Task RunAsync(Func<Uri> baseAddress, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(SocketUri(baseAddress()), cancellationToken);
                await ReceiveAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Cider is not running or dropped the connection; REST polling still works.
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var frame = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            switch (frame)
            {
                case ['0', ..]:
                    // Engine.IO open: join the default Socket.IO namespace.
                    await SendAsync(socket, "40", cancellationToken);
                    break;
                case "2":
                    await SendAsync(socket, "3", cancellationToken);
                    break;
                case ['4', '1', ..]:
                    return;
                default:
                    if (TryParseTimeEvent(frame, out var sample))
                    {
                        TimeChanged?.Invoke(sample);
                    }

                    break;
            }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
}
