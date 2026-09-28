using System.Net.Http;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lyrider.Services;

/// <summary>
/// Owns the foreground artwork and blurred composition backdrop, including one preloaded next
/// artwork. Keeping both image pipelines here lets a track transition promote the exact decoded
/// resources that were prepared from the queue instead of starting two new URI loads.
/// </summary>
public sealed class ArtworkPresenter : IDisposable
{
    private const string SourceParameter = "Backdrop";
    private const string BlurEffectName = "Blur";
    private const string BlurAmountProperty = BlurEffectName + ".BlurAmount";
    private const float MaximumBlurAmount = 16;
    private static readonly HttpClient ArtworkClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly FrameworkElement _host;
    private readonly Image _artworkImage;
    private readonly Image _queueArtworkImage;
    private readonly CompositionSurfaceBrush _surfaceBrush;
    private readonly CompositionEffectBrush _effectBrush;
    private readonly SpriteVisual _visual;

    private LoadedImageSurface? _surface;
    private CancellationTokenSource? _bitmapLoadCancellation;
    private string? _artworkUrl;
    private BitmapImage? _nextBitmap;
    private LoadedImageSurface? _nextSurface;
    private CancellationTokenSource? _nextBitmapLoadCancellation;
    private string? _nextArtworkUrl;
    private bool _nextBitmapFailed;
    private bool _nextSurfaceFailed;
    private bool _disposed;

    public ArtworkPresenter(
        FrameworkElement host,
        Image artworkImage,
        Image queueArtworkImage)
    {
        _host = host;
        _artworkImage = artworkImage;
        _queueArtworkImage = queueArtworkImage;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

        var blur = new GaussianBlurEffect
        {
            Name = BlurEffectName,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter(SourceParameter)
        };
        _surfaceBrush = compositor.CreateSurfaceBrush();
        _surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        _surfaceBrush.AnchorPoint = new Vector2(0.5f);
        _effectBrush = compositor.CreateEffectFactory(blur, [BlurAmountProperty]).CreateBrush();
        _effectBrush.SetSourceParameter(SourceParameter, _surfaceBrush);

        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _effectBrush;
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);

        host.SizeChanged += Host_SizeChanged;
        UpdateSurfaceAlignment();
    }

    public void PrepareNext(string? url)
    {
        if (_disposed)
        {
            return;
        }

        if (string.Equals(url, _artworkUrl, StringComparison.Ordinal))
        {
            ClearNext();
            return;
        }

        if (string.Equals(url, _nextArtworkUrl, StringComparison.Ordinal))
        {
            return;
        }

        ClearNext();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var artworkUri))
        {
            return;
        }

        _nextArtworkUrl = url;
        var bitmap = new BitmapImage();
        bitmap.ImageFailed += (_, _) =>
        {
            if (ReferenceEquals(_nextBitmap, bitmap))
            {
                _nextBitmapFailed = true;
            }
        };
        _nextBitmap = bitmap;
        _nextBitmapLoadCancellation = new CancellationTokenSource();
        _ = LoadNextBitmapAsync(bitmap, artworkUri, _nextBitmapLoadCancellation.Token);

        var surface = LoadedImageSurface.StartLoadFromUri(artworkUri);
        surface.LoadCompleted += (_, args) =>
        {
            if (ReferenceEquals(_nextSurface, surface) &&
                args.Status != LoadedImageSourceLoadStatus.Success)
            {
                _nextSurfaceFailed = true;
            }
        };
        _nextSurface = surface;
    }

    private async Task LoadNextBitmapAsync(BitmapImage bitmap, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            // A URI-backed BitmapImage defers loading until it is displayed. Loading a stream
            // explicitly ensures the next cover is downloaded and decoded before Show promotes it.
            var bytes = await ArtworkClient.GetByteArrayAsync(uri, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The queue changed, the displayed cover changed, or the presenter was disposed.
        }
        catch (Exception)
        {
            if (ReferenceEquals(_nextBitmap, bitmap))
            {
                _nextBitmapFailed = true;
            }
            else if (!_disposed && !cancellationToken.IsCancellationRequested &&
                ReferenceEquals(_artworkImage.Source, bitmap))
            {
                // The track may have started while the preload was still in flight.
                bitmap.UriSource = uri;
            }
        }
    }

    public void Show(string? url)
    {
        if (_disposed || string.Equals(_artworkUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _artworkUrl = url;
        CancelBitmapLoad(ref _bitmapLoadCancellation);
        BitmapImage? bitmap = null;
        LoadedImageSurface? surface = null;
        var promoted = string.Equals(url, _nextArtworkUrl, StringComparison.Ordinal);

        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var artworkUri))
        {
            bitmap = promoted && !_nextBitmapFailed
                ? _nextBitmap
                : new BitmapImage(artworkUri);
            surface = promoted && !_nextSurfaceFailed
                ? _nextSurface
                : LoadedImageSurface.StartLoadFromUri(artworkUri);
            if (promoted && !_nextBitmapFailed)
            {
                _bitmapLoadCancellation = _nextBitmapLoadCancellation;
                _nextBitmapLoadCancellation = null;
            }
        }

        _artworkImage.Source = bitmap;
        _queueArtworkImage.Source = bitmap;

        var previous = _surface;
        _surface = surface;
        _surfaceBrush.Surface = surface;
        if (!ReferenceEquals(previous, surface))
        {
            previous?.Dispose();
        }

        if (promoted)
        {
            CancelBitmapLoad(ref _nextBitmapLoadCancellation);
            if (!ReferenceEquals(_nextSurface, surface))
            {
                _nextSurface?.Dispose();
            }

            _nextBitmap = null;
            _nextSurface = null;
            _nextArtworkUrl = null;
            _nextBitmapFailed = false;
            _nextSurfaceFailed = false;
        }
        else
        {
            ClearNext();
        }
    }

    public void Apply(double opacity, double blurPercentage)
    {
        _visual.Opacity = (float)Math.Clamp(opacity, 0, 1);
        _effectBrush.Properties.InsertScalar(
            BlurAmountProperty,
            (float)(Math.Clamp(blurPercentage, 0, 100) / 100 * MaximumBlurAmount));
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceAlignment();

    private void UpdateSurfaceAlignment()
    {
        var size = new Vector2((float)_host.ActualWidth, (float)_host.ActualHeight);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        _surfaceBrush.CenterPoint = size / 2;
        _surfaceBrush.Offset = size / 2;
    }

    private void ClearNext()
    {
        CancelBitmapLoad(ref _nextBitmapLoadCancellation);
        _nextSurface?.Dispose();
        _nextSurface = null;
        _nextBitmap = null;
        _nextArtworkUrl = null;
        _nextBitmapFailed = false;
        _nextSurfaceFailed = false;
    }

    private static void CancelBitmapLoad(ref CancellationTokenSource? cancellation)
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelBitmapLoad(ref _bitmapLoadCancellation);
        _host.SizeChanged -= Host_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _artworkImage.Source = null;
        _queueArtworkImage.Source = null;
        _surface?.Dispose();
        _surface = null;
        ClearNext();
        _surfaceBrush.Dispose();
        _effectBrush.Dispose();
        _visual.Dispose();
    }
}
