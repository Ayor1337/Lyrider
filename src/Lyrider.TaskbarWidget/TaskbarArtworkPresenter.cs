using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace Lyrider.TaskbarWidget;

internal sealed class TaskbarArtworkPresenter(Border border, TextBlock placeholder) : IDisposable
{
    private static readonly HttpClient ArtworkClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Entry? _current;
    private Entry? _next;
    private bool _disposed;

    public void PrepareNext(string? url)
    {
        if (_disposed || string.Equals(_next?.Url, url, StringComparison.Ordinal))
        {
            return;
        }

        _next?.Dispose();
        _next = null;
        if (TryGetUri(url, out var uri) && !string.Equals(_current?.Url, url, StringComparison.Ordinal))
        {
            _next = StartLoad(uri!);
        }
    }

    public void Show(string? url)
    {
        if (_disposed || (_current is not null && string.Equals(_current.Url, url, StringComparison.Ordinal)))
        {
            return;
        }

        _current?.Dispose();
        _current = null;
        if (!TryGetUri(url, out var uri))
        {
            _next?.Dispose();
            _next = null;
            ShowPlaceholder();
            return;
        }

        if (_next is { Failed: false } next && string.Equals(next.Url, url, StringComparison.Ordinal))
        {
            _current = next;
            _next = null;
        }
        else
        {
            _current = StartLoad(uri!);
        }

        if (_current.Bitmap is { } bitmap)
        {
            ShowBitmap(bitmap);
        }
        else
        {
            ShowPlaceholder();
        }
    }

    private Entry StartLoad(Uri uri)
    {
        var entry = new Entry(uri.OriginalString);
        _ = LoadAsync(entry, uri);
        return entry;
    }

    private async Task LoadAsync(Entry entry, Uri uri)
    {
        var token = entry.Cancellation.Token;
        try
        {
            var bytes = await ArtworkClient.GetByteArrayAsync(uri, token);
            // WPF 的 URI 图片会延迟加载；从流解码后再缓存，切歌时才能直接显示。
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                image.Freeze();
                return image;
            }, token);
            if (_disposed || token.IsCancellationRequested)
            {
                return;
            }

            entry.Bitmap = bitmap;
            if (ReferenceEquals(_current, entry))
            {
                ShowBitmap(bitmap);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            entry.Failed = true;
            if (!_disposed && ReferenceEquals(_current, entry))
            {
                ShowPlaceholder();
            }
        }
    }

    private void ShowBitmap(BitmapImage bitmap)
    {
        border.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        placeholder.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder()
    {
        border.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        placeholder.Visibility = Visibility.Visible;
    }

    private static bool TryGetUri(string? url, out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public void Dispose()
    {
        _disposed = true;
        _current?.Dispose();
        _next?.Dispose();
        _current = null;
        _next = null;
    }

    private sealed class Entry(string url) : IDisposable
    {
        public string Url { get; } = url;
        public CancellationTokenSource Cancellation { get; } = new();
        public BitmapImage? Bitmap { get; set; }
        public bool Failed { get; set; }

        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }
}
