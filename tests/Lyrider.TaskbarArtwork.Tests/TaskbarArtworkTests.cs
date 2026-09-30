using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lyrider.TaskbarWidget;

namespace Lyrider.TaskbarArtwork.Tests;

internal static class TaskbarArtworkTests
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var server = new ArtworkServer();
                await SetArtwork_PreloadedCover_DoesNotShowPlaceholder(server);
                await SetArtwork_InFlightPromotion_CompletesWhileFollowingCoverLoads(server);
                await PrepareArtwork_QueueReplaced_CancelsObsoleteLoad(server);
                await SetArtwork_FailedPreload_RetriesWhenTrackStarts(server);
                await SetArtwork_FailedCurrentLoad_ShowsPlaceholder(server);
                await Close_InFlightLoad_CancelsRequest(server);
                Console.WriteLine("PASS: 6 taskbar artwork integration tests.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();
    }

    private static async Task SetArtwork_PreloadedCover_DoesNotShowPlaceholder(ArtworkServer server)
    {
        var taskbarCover = server.Add("next-160");
        var window = new TaskbarWidgetWindow();
        try
        {
            window.PrepareArtwork(taskbarCover.Url);
            await UntilAsync(() => PreparedBitmap(window) is not null);
            var bitmap = PreparedBitmap(window);
            window.PrepareArtwork(taskbarCover.Url);
            SetArtwork(window, taskbarCover.Url);
            Check(Placeholder(window).Visibility == Visibility.Collapsed,
                "切歌时已预加载的封面仍显示 fallback，占位图未同步隐藏。");
            Check(Cover(window).Background is ImageBrush, "切歌时未显示图片画刷。");
            Check(ReferenceEquals(bitmap, ((ImageBrush)Cover(window).Background).ImageSource), "切歌未复用解码图片。");
            await Task.Delay(50);
            Check(taskbarCover.Requests == 1, "切歌重新下载了预加载封面。");
            SavePreview(window);
            Console.WriteLine("PASS: 已预加载的封面在切歌时直接显示。");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task SetArtwork_InFlightPromotion_CompletesWhileFollowingCoverLoads(ArtworkServer server)
    {
        var response = server.Add("in-flight", delayed: true);
        var following = server.Add("following");
        var window = new TaskbarWidgetWindow();
        try
        {
            window.PrepareArtwork(response.Url);
            await UntilAsync(() => response.Requests == 1);
            var token = EntryToken(NextEntry(window)!);
            SetArtwork(window, response.Url);
            window.PrepareArtwork(following.Url);
            Check(!token.IsCancellationRequested, "准备下一首取消了当前封面加载。");
            await UntilAsync(() => PreparedBitmap(window) is not null);
            var followingBitmap = PreparedBitmap(window);
            Check(Placeholder(window).Visibility == Visibility.Visible, "下一首封面覆盖了仍在加载的当前封面。");
            response.Release();
            await UntilAsync(() => Placeholder(window).Visibility == Visibility.Collapsed);
            Check(response.Requests == 1, "接手未完成的预加载时重复下载。");
            Check(!ReferenceEquals(followingBitmap, ((ImageBrush)Cover(window).Background).ImageSource), "当前封面显示为下一首。");
            SetArtwork(window, following.Url);
            Check(ReferenceEquals(followingBitmap, ((ImageBrush)Cover(window).Background).ImageSource), "后续切歌未复用已准备的封面。");
            Console.WriteLine("PASS: 接手未完成的下载后，仍可预加载下一首。");
        }
        finally { window.Close(); }
    }

    private static async Task PrepareArtwork_QueueReplaced_CancelsObsoleteLoad(ArtworkServer server)
    {
        var obsolete = server.Add("obsolete", delayed: true);
        var replacement = server.Add("replacement");
        var window = new TaskbarWidgetWindow();
        try
        {
            window.PrepareArtwork(obsolete.Url);
            await UntilAsync(() => obsolete.Requests == 1);
            var token = EntryToken(NextEntry(window)!);
            window.PrepareArtwork(replacement.Url);
            Check(token.IsCancellationRequested, "队列变化未取消旧封面下载。");
            obsolete.Release();
            await UntilAsync(() => PreparedBitmap(window) is not null);
            var bitmap = PreparedBitmap(window);
            SetArtwork(window, replacement.Url);
            Check(ReferenceEquals(bitmap, ((ImageBrush)Cover(window).Background).ImageSource), "旧队列的结果覆盖了新封面。");
            SetArtwork(window, null);
            Check(Placeholder(window).Visibility == Visibility.Visible && Cover(window).Background is SolidColorBrush,
                "清空当前歌曲后仍显示旧封面。");
            SetArtwork(window, "invalid");
            Check(Placeholder(window).Visibility == Visibility.Visible, "无效地址未使用 fallback。");
            Console.WriteLine("PASS: 队列替换取消旧下载，空歌曲和无效地址使用 fallback。");
        }
        finally { window.Close(); }
    }

    private static async Task SetArtwork_FailedPreload_RetriesWhenTrackStarts(ArtworkServer server)
    {
        var response = server.Add("retry", status: "403 Forbidden");
        var window = new TaskbarWidgetWindow();
        try
        {
            window.PrepareArtwork(response.Url);
            await UntilAsync(() => EntryFailed(NextEntry(window)!));
            response.Status = "200 OK";
            SetArtwork(window, response.Url);
            await UntilAsync(() => Placeholder(window).Visibility == Visibility.Collapsed);
            Check(response.Requests == 2, "预加载失败后，播放当前歌曲未重试。");
            Console.WriteLine("PASS: 预加载 HTTP 403 后，切歌时重新加载成功。");
        }
        finally { window.Close(); }
    }

    private static async Task SetArtwork_FailedCurrentLoad_ShowsPlaceholder(ArtworkServer server)
    {
        foreach (var status in new[] { "401 Unauthorized", "200 OK" })
        {
            var response = server.Add($"invalid-{status[0]}", status: status, malformed: true);
            var window = new TaskbarWidgetWindow();
            try
            {
                SetArtwork(window, response.Url);
                await UntilAsync(() => EntryFailed(CurrentEntry(window)!));
                Check(Placeholder(window).Visibility == Visibility.Visible && Cover(window).Background is SolidColorBrush,
                    "加载失败后未显示 fallback。");
            }
            finally { window.Close(); }
        }
        Console.WriteLine("PASS: HTTP 401 和无效图片保持 fallback。");
    }

    private static async Task Close_InFlightLoad_CancelsRequest(ArtworkServer server)
    {
        var response = server.Add("closed", delayed: true);
        var window = new TaskbarWidgetWindow();
        SetArtwork(window, response.Url);
        await UntilAsync(() => response.Requests == 1);
        var token = EntryToken(CurrentEntry(window)!);
        window.Close();
        Check(token.IsCancellationRequested, "关闭窗口未取消封面请求。");
        response.Release();
        Console.WriteLine("PASS: 关闭窗口取消未完成的封面请求。");
    }

    private static void SavePreview(TaskbarWidgetWindow window)
    {
        var cover = Cover(window);
        cover.Measure(new Size(36, 36));
        cover.Arrange(new Rect(0, 0, 36, 36));
        var image = new RenderTargetBitmap(144, 144, 384, 384, PixelFormats.Pbgra32);
        image.Render(cover);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "preloaded-cover.png"));
        encoder.Save(output);
    }

    private static void SetArtwork(TaskbarWidgetWindow window, string? url) =>
        typeof(TaskbarWidgetWindow).GetMethod("SetArtwork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { url });

    private static Border Cover(TaskbarWidgetWindow window) => (Border)window.FindName("ArtworkBorder");
    private static TextBlock Placeholder(TaskbarWidgetWindow window) => (TextBlock)window.FindName("ArtworkPlaceholder");

    private static object? PreparedBitmap(TaskbarWidgetWindow window)
    {
        var next = NextEntry(window);
        return next?.GetType().GetProperty("Bitmap")!.GetValue(next);
    }

    private static object? NextEntry(TaskbarWidgetWindow window) => Entry(window, "_next");
    private static object? CurrentEntry(TaskbarWidgetWindow window) => Entry(window, "_current");
    private static bool EntryFailed(object entry) => (bool)entry.GetType().GetProperty("Failed")!.GetValue(entry)!;
    private static CancellationToken EntryToken(object entry) =>
        ((CancellationTokenSource)entry.GetType().GetProperty("Cancellation")!.GetValue(entry)!).Token;

    private static object? Entry(TaskbarWidgetWindow window, string field)
    {
        var presenter = typeof(TaskbarWidgetWindow).GetField("_artworkPresenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        return presenter.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(condition(), "等待封面加载超时。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class ArtworkServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, Response> _responses = new();
    private readonly byte[] _image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixture.png"));

    public ArtworkServer()
    {
        _listener.Start();
        _ = AcceptAsync(_cancellation.Token);
    }

    public Response Add(string name, bool delayed = false, string status = "200 OK", bool malformed = false)
    {
        var path = $"/{name}.png";
        var response = new Response($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}", status, malformed);
        _responses[path] = response;
        if (!delayed) response.Release();
        return response;
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = RespondAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }

    private async Task RespondAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(token);
                if (request is null) return;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
                var response = _responses[request.Split(' ')[1]];
                Interlocked.Increment(ref response.Requests);
                await response.Ready.Task.WaitAsync(token);
                var body = response.Malformed ? Encoding.ASCII.GetBytes("invalid image") : _image;
                var header = $"HTTP/1.1 {response.Status}\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                await stream.WriteAsync(body, token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        _cancellation.Dispose();
    }

    internal sealed class Response(string url, string status, bool malformed)
    {
        public string Url { get; } = url;
        public int Requests;
        public volatile string Status = status;
        public bool Malformed { get; } = malformed;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Ready.TrySetResult();
    }
}
