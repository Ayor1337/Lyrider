using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Lyrider.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lyrider.Artwork.Tests;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // WinUI executables have no attached console; the runner prints this log after exit.
        Console.SetOut(new StreamWriter(Path.Combine(AppContext.BaseDirectory, "results.log")) { AutoFlush = true });
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new ArtworkPresenterTests();
        });
    }
}

internal sealed class ArtworkPresenterTests : Application
{
    private Window? _window;
    private readonly Grid _host = new();
    private readonly Image _foreground = new();
    private readonly Image _queue = new();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var root = new Grid();
            root.Children.Add(_host);
            root.Children.Add(_foreground);
            root.Children.Add(_queue);
            _window = new Window { Content = root };
            using var server = new ArtworkServer();
            await PrepareNext_HttpArtwork_DecodesBeforeShow(server);
            await Show_PreloadInFlight_CompletesAfterPreparingFollowingTrack(server);
            await PrepareNext_QueueReplaced_CancelsObsoleteLoad(server);
            await Show_PreloadFailed_CreatesFallbackResources(server);
            await Show_PromotedLoadFails_FallsBackToUri(server);
            await Dispose_PreloadInFlight_CancelsLoad(server);
            Console.WriteLine("PASS: 6 artwork integration tests.");
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL: {exception}");
            Environment.ExitCode = 1;
        }
        finally
        {
            _window?.Close();
            Exit();
        }
    }

    private ArtworkPresenter CreatePresenter() => new(_host, _foreground, _queue);

    private async Task PrepareNext_HttpArtwork_DecodesBeforeShow(ArtworkServer server)
    {
        using var presenter = CreatePresenter();
        var response = server.Add("ready");
        presenter.PrepareNext(response.Url);
        var bitmap = Field<BitmapImage>(presenter, "_nextBitmap");
        var surface = Field<LoadedImageSurface>(presenter, "_nextSurface");
        await UntilAsync(() => bitmap.PixelWidth > 0 && surface.DecodedPhysicalSize.Width > 0,
            "The next foreground cover must be decoded before Show.");
        presenter.PrepareNext(response.Url);
        Check(ReferenceEquals(bitmap, Field<BitmapImage>(presenter, "_nextBitmap")), "Repeated queue refresh replaced the preload.");
        var requests = response.Requests;
        presenter.Show(response.Url);
        Check(ReferenceEquals(bitmap, _foreground.Source) && ReferenceEquals(bitmap, _queue.Source), "Show did not reuse the decoded bitmap.");
        Check(ReferenceEquals(surface, Field<LoadedImageSurface>(presenter, "_surface")), "Show did not reuse the background surface.");
        await Task.Delay(150);
        Check(response.Requests == requests, "Show fetched an already preloaded cover again.");
        Console.WriteLine($"PASS: decoded {bitmap.PixelWidth}x{bitmap.PixelHeight} before Show; no extra HTTP request on transition.");
    }

    private async Task Show_PreloadInFlight_CompletesAfterPreparingFollowingTrack(ArtworkServer server)
    {
        using var presenter = CreatePresenter();
        var response = server.Add("in-flight", delayed: true);
        presenter.PrepareNext(response.Url);
        var bitmap = Field<BitmapImage>(presenter, "_nextBitmap");
        await UntilAsync(() => response.Requests >= 2, "Both image pipelines must begin before Show.");
        presenter.Show(response.Url);
        presenter.PrepareNext(server.Add("following").Url);
        response.Release();
        await UntilAsync(() => bitmap.PixelWidth > 0, "Promoted load was canceled when the following track was prepared.");
        Check(ReferenceEquals(bitmap, _foreground.Source), "Following track overwrote the current cover.");
        Console.WriteLine("PASS: in-flight promotion survives preparing the following track.");
    }

    private async Task PrepareNext_QueueReplaced_CancelsObsoleteLoad(ArtworkServer server)
    {
        using var presenter = CreatePresenter();
        var response = server.Add("obsolete", delayed: true);
        presenter.PrepareNext(response.Url);
        var token = Field<CancellationTokenSource>(presenter, "_nextBitmapLoadCancellation").Token;
        await UntilAsync(() => response.Requests >= 2, "Obsolete preload did not start.");
        var replacement = server.Add("replacement");
        presenter.PrepareNext(replacement.Url);
        Check(token.IsCancellationRequested, "Queue replacement did not cancel the old load.");
        response.Release();
        var bitmap = Field<BitmapImage>(presenter, "_nextBitmap");
        await UntilAsync(() => bitmap.PixelWidth > 0, "Replacement cover did not decode.");
        presenter.Show(replacement.Url);
        Check(ReferenceEquals(bitmap, _foreground.Source), "Obsolete result replaced the selected cover.");
        presenter.Show(null);
        Check(_foreground.Source is null && _queue.Source is null, "Clearing the current track retained its cover.");
        Console.WriteLine("PASS: queue replacement cancels obsolete work; clearing removes the cover.");
    }

    private async Task Show_PreloadFailed_CreatesFallbackResources(ArtworkServer server)
    {
        using var presenter = CreatePresenter();
        var response = server.Add("failure", fail: true);
        presenter.PrepareNext(response.Url);
        var failedBitmap = Field<BitmapImage>(presenter, "_nextBitmap");
        var failedSurface = Field<LoadedImageSurface>(presenter, "_nextSurface");
        await UntilAsync(() => Field<bool>(presenter, "_nextBitmapFailed") && Field<bool>(presenter, "_nextSurfaceFailed"), "Failed preload was not detected.");
        response.Fail = false;
        presenter.Show(response.Url);
        Check(!ReferenceEquals(failedBitmap, _foreground.Source), "Failed bitmap was promoted instead of retried.");
        Check(((BitmapImage)_foreground.Source).UriSource.AbsoluteUri == response.Url, "Failed preload did not fall back to URI loading.");
        Check(!ReferenceEquals(failedSurface, Field<LoadedImageSurface>(presenter, "_surface")), "Failed background surface was reused.");
        Console.WriteLine("PASS: failed preload creates fallback foreground and background resources.");
    }

    private async Task Show_PromotedLoadFails_FallsBackToUri(ArtworkServer server)
    {
        using var presenter = CreatePresenter();
        var response = server.Add("late-failure", delayed: true, fail: true);
        presenter.PrepareNext(response.Url);
        var bitmap = Field<BitmapImage>(presenter, "_nextBitmap");
        await UntilAsync(() => response.Requests >= 2, "Delayed preload did not start.");
        presenter.Show(response.Url);
        response.Release();
        await UntilAsync(() => bitmap.UriSource?.AbsoluteUri == response.Url, "Promoted failure did not fall back to normal URI loading.");
        Console.WriteLine("PASS: failure after promotion falls back to normal cover loading.");
    }

    private async Task Dispose_PreloadInFlight_CancelsLoad(ArtworkServer server)
    {
        using var presenter = CreatePresenter();
        var response = server.Add("disposed", delayed: true);
        presenter.PrepareNext(response.Url);
        var token = Field<CancellationTokenSource>(presenter, "_nextBitmapLoadCancellation").Token;
        await UntilAsync(() => response.Requests >= 2, "Preload did not start before disposal.");
        presenter.Dispose();
        Check(token.IsCancellationRequested, "Disposal did not cancel the pending load.");
        response.Release();
        Check(_foreground.Source is null, "Disposal left a foreground cover attached.");
        Console.WriteLine("PASS: disposal cancels pending work.");
    }

    private static T Field<T>(ArtworkPresenter presenter, string name) =>
        (T)typeof(ArtworkPresenter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter)!;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task UntilAsync(Func<bool> predicate, string message)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!predicate())
        {
            Check(Environment.TickCount64 < deadline, message);
            await Task.Delay(20);
        }
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

    public Response Add(string name, bool delayed = false, bool fail = false)
    {
        var path = $"/{name}.png";
        var response = new Response($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}", fail);
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
                var body = response.Fail ? Array.Empty<byte>() : _image;
                var status = response.Fail ? "404 Not Found" : "200 OK";
                var header = $"HTTP/1.1 {status}\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
                await stream.WriteAsync(body, token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // A canceled preload closes its connection.
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        _cancellation.Dispose();
    }

    internal sealed class Response(string url, bool fail)
    {
        public string Url { get; } = url;
        public volatile bool Fail = fail;
        public int Requests;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Ready.TrySetResult();
    }
}
