using System.Net.Http;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Lyrider.TaskbarWidget;
using Windows.Foundation;

namespace Lyrider.Services;

/// <summary>
/// Owns the foreground artwork and blurred composition backdrop, including one preloaded next
/// artwork. Keeping both image pipelines here lets a track transition promote the exact decoded
/// resources that were prepared from the queue instead of starting two new URI loads.
/// The backdrop alternates between two layers so a track change cross-fades instead of cutting.
/// </summary>
public sealed class ArtworkPresenter : IDisposable
{
    private const string SourceParameter = "Backdrop";
    private const string BlurEffectName = "Blur";
    private const string BlurAmountProperty = BlurEffectName + ".BlurAmount";
    private const float MaximumBlurAmount = 64;
    private const int CrossFadeMilliseconds = 600;
    // The backdrop is stretched across the window and blurred, so a small decode looks the same
    // and keeps the blur input cheap.
    private static readonly Size BackdropDecodeSize = new(128, 128);
    private static readonly HttpClient ArtworkClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly FrameworkElement _host;
    private readonly Image _artworkImage;
    private readonly Image _queueArtworkImage;
    private readonly ArtworkCache? _cache;
    private readonly ContainerVisual _backdrop;
    private BackdropLayer _frontLayer;
    private BackdropLayer _backLayer;

    private LoadedImageSurface? _surface;
    // The previous cover, still shown by the back layer while it fades out.
    private LoadedImageSurface? _fadingSurface;
    private int _backdropVersion;
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
        Image queueArtworkImage,
        ArtworkCache? cache = null)
    {
        _host = host;
        _artworkImage = artworkImage;
        _queueArtworkImage = queueArtworkImage;
        _cache = cache;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

        var blur = new GaussianBlurEffect
        {
            Name = BlurEffectName,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter(SourceParameter)
        };
        var effectFactory = compositor.CreateEffectFactory(blur, [BlurAmountProperty]);
        _backLayer = new BackdropLayer(compositor, effectFactory) { Opacity = 0 };
        _frontLayer = new BackdropLayer(compositor, effectFactory);

        _backdrop = compositor.CreateContainerVisual();
        _backdrop.RelativeSizeAdjustment = Vector2.One;
        _backdrop.Children.InsertAtTop(_backLayer.Visual);
        _backdrop.Children.InsertAtTop(_frontLayer.Visual);
        ElementCompositionPreview.SetElementChildVisual(host, _backdrop);

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

        var surface = StartSurfaceLoad(artworkUri, ReadCached(artworkUri));
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
            var bytes = _cache is null
                ? await ArtworkClient.GetByteArrayAsync(uri, cancellationToken)
                : await _cache.GetBytesAsync(uri, cancellationToken);
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
            // 没命中缓存时这里是下载失败，没有文件可删；命中时说明缓存文件解不出图片。
            _cache?.Remove(uri);
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

    private byte[]? ReadCached(Uri uri) =>
        _cache is not null && _cache.TryReadCached(uri, out var bytes) ? bytes : null;

    /// <summary>
    /// 命中缓存时从内存解码；未命中时沿用 URI 加载（保持原有的失败回退），同时在后台把图片存进缓存，
    /// 下次启动或重播就不用再下载。
    /// </summary>
    private BitmapImage CreateBitmap(Uri uri, byte[]? cached)
    {
        if (cached is null)
        {
            if (_cache is not null)
            {
                _ = StoreInCacheAsync(_cache, uri);
            }

            return new BitmapImage(uri);
        }

        var bitmap = new BitmapImage();
        _ = SetCachedSourceAsync(bitmap, cached, uri);
        return bitmap;
    }

    private async Task SetCachedSourceAsync(BitmapImage bitmap, byte[] bytes, Uri uri)
    {
        try
        {
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception)
        {
            _cache?.Remove(uri);
            if (!_disposed)
            {
                bitmap.UriSource = uri;
            }
        }
    }

    private static async Task StoreInCacheAsync(ArtworkCache cache, Uri uri)
    {
        try
        {
            await cache.GetBytesAsync(uri);
        }
        catch (Exception)
        {
            // 只是没存上；显示走的是 URI 加载，不受影响。
        }
    }

    private static LoadedImageSurface StartSurfaceLoad(Uri uri, byte[]? cached) => cached is null
        ? LoadedImageSurface.StartLoadFromUri(uri, BackdropDecodeSize)
        : LoadedImageSurface.StartLoadFromStream(new MemoryStream(cached).AsRandomAccessStream(), BackdropDecodeSize);

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
            var cached = promoted && !_nextBitmapFailed && !_nextSurfaceFailed ? null : ReadCached(artworkUri);
            bitmap = promoted && !_nextBitmapFailed
                ? _nextBitmap
                : CreateBitmap(artworkUri, cached);
            surface = promoted && !_nextSurfaceFailed
                ? _nextSurface
                : StartSurfaceLoad(artworkUri, cached);
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
        if (!ReferenceEquals(previous, surface))
        {
            ShowBackdrop(surface, previous);
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
        _backdrop.Opacity = (float)Math.Clamp(opacity, 0, 1);
        var blurAmount = (float)(Math.Clamp(blurPercentage, 0, 100) / 100 * MaximumBlurAmount);
        _frontLayer.SetBlurAmount(blurAmount);
        _backLayer.SetBlurAmount(blurAmount);
    }

    private void ShowBackdrop(LoadedImageSurface? surface, LoadedImageSurface? previous)
    {
        (_frontLayer, _backLayer) = (_backLayer, _frontLayer);
        _frontLayer.Visual.StopAnimation(nameof(Visual.Opacity));
        _frontLayer.Opacity = 0;
        _frontLayer.Surface = surface;
        _backdrop.Children.Remove(_frontLayer.Visual);
        _backdrop.Children.InsertAtTop(_frontLayer.Visual);

        // The back layer no longer shows the cover it faded out before this swap.
        if (!ReferenceEquals(_fadingSurface, surface))
        {
            _fadingSurface?.Dispose();
        }

        _fadingSurface = previous;
        var version = ++_backdropVersion;
        if (surface is null || surface.DecodedPhysicalSize.Width > 0)
        {
            CrossFade();
            return;
        }

        // Keep the previous backdrop until the new cover can actually be drawn.
        surface.LoadCompleted += (_, _) =>
        {
            if (!_disposed && version == _backdropVersion)
            {
                CrossFade();
            }
        };
    }

    private void CrossFade()
    {
        if (!UiMotion.IsEnabled)
        {
            _backLayer.Visual.StopAnimation(nameof(Visual.Opacity));
            _frontLayer.Opacity = 1;
            _backLayer.Opacity = 0;
            return;
        }

        // The incoming layer rises quickly while the outgoing one lingers, so the two never
        // both sit near half opacity and let the backdrop dip in the middle of the fade.
        FadeTo(_frontLayer.Visual, 1, new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1));
        FadeTo(_backLayer.Visual, 0, new Vector2(0.7f, 0), new Vector2(0.84f, 0));
    }

    private static void FadeTo(Visual visual, float opacity, Vector2 controlPoint1, Vector2 controlPoint2)
    {
        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, opacity, compositor.CreateCubicBezierEasingFunction(controlPoint1, controlPoint2));
        animation.Duration = TimeSpan.FromMilliseconds(CrossFadeMilliseconds);
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceAlignment();

    private void UpdateSurfaceAlignment()
    {
        var size = new Vector2((float)_host.ActualWidth, (float)_host.ActualHeight);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        _frontLayer.Align(size);
        _backLayer.Align(size);
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
        _fadingSurface?.Dispose();
        _fadingSurface = null;
        ClearNext();
        _frontLayer.Dispose();
        _backLayer.Dispose();
        _backdrop.Dispose();
    }

    private sealed class BackdropLayer : IDisposable
    {
        private readonly CompositionSurfaceBrush _surfaceBrush;
        private readonly CompositionEffectBrush _effectBrush;

        public BackdropLayer(Compositor compositor, CompositionEffectFactory effectFactory)
        {
            _surfaceBrush = compositor.CreateSurfaceBrush();
            _surfaceBrush.Stretch = CompositionStretch.UniformToFill;
            _surfaceBrush.AnchorPoint = new Vector2(0.5f);
            _effectBrush = effectFactory.CreateBrush();
            _effectBrush.SetSourceParameter(SourceParameter, _surfaceBrush);

            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = _effectBrush;
            Visual.RelativeSizeAdjustment = Vector2.One;
        }

        public SpriteVisual Visual { get; }

        public float Opacity
        {
            set => Visual.Opacity = value;
        }

        public LoadedImageSurface? Surface
        {
            set => _surfaceBrush.Surface = value;
        }

        public void SetBlurAmount(float blurAmount) =>
            _effectBrush.Properties.InsertScalar(BlurAmountProperty, blurAmount);

        public void Align(Vector2 size)
        {
            _surfaceBrush.CenterPoint = size / 2;
            _surfaceBrush.Offset = size / 2;
        }

        public void Dispose()
        {
            _surfaceBrush.Dispose();
            _effectBrush.Dispose();
            Visual.Dispose();
        }
    }
}
