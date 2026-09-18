using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Lyrider.Services;

/// <summary>
/// Draws the now-playing artwork behind the player as a blurred composition layer. XAML has no
/// blur of its own, so the cover is uploaded once as a composition surface and softened by a
/// Win2D effect brush; the radius can then move without decoding the image again.
/// </summary>
public sealed class ArtworkBackdrop : IDisposable
{
    private const string SourceParameter = "Backdrop";
    private const string BlurEffectName = "Blur";
    private const string BlurAmountProperty = BlurEffectName + ".BlurAmount";

    /// <summary>
    /// Win2D blur radius in DIPs that the 100% slider position maps to. Win2D's radius grows
    /// faster than it reads, so a larger ceiling would flatten the artwork long before the
    /// slider reached the top and leave the upper half doing nothing.
    /// </summary>
    private const float MaximumBlurAmount = 16;

    private readonly FrameworkElement _host;
    private readonly CompositionSurfaceBrush _surfaceBrush;
    private readonly CompositionEffectBrush _effectBrush;
    private readonly SpriteVisual _visual;

    private LoadedImageSurface? _surface;
    private string? _artworkUrl;
    private bool _disposed;

    public ArtworkBackdrop(FrameworkElement host)
    {
        _host = host;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

        var blur = new GaussianBlurEffect
        {
            Name = BlurEffectName,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter(SourceParameter)
        };
        _surfaceBrush = compositor.CreateSurfaceBrush();
        // Artwork is rarely the window's aspect ratio, so scale it to cover and centre the
        // overflow; a stretched fill would visibly squash the cover.
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

    public void SetArtwork(string? url)
    {
        if (string.Equals(_artworkUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _artworkUrl = url;
        var previous = _surface;
        _surface = null;
        _surfaceBrush.Surface = null;
        previous?.Dispose();

        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var artworkUri))
        {
            return;
        }

        var surface = LoadedImageSurface.StartLoadFromUri(artworkUri);
        _surface = surface;
        _surfaceBrush.Surface = surface;
    }

    public void Apply(double opacity, double blurPercentage)
    {
        _visual.Opacity = (float)Math.Clamp(opacity, 0, 1);
        // Written on every call, including 0: the effect would otherwise keep Win2D's default
        // radius of 3 DIPs, so "no blur" would still be visibly soft.
        _effectBrush.Properties.InsertScalar(
            BlurAmountProperty,
            (float)(Math.Clamp(blurPercentage, 0, 100) / 100 * MaximumBlurAmount));
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceAlignment();

    /// <summary>
    /// A UniformToFill surface brush grows away from its anchor, so the overflow only stays
    /// centred while the brush is pinned to the middle of the visual instead of its corner.
    /// </summary>
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.SizeChanged -= Host_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _surface?.Dispose();
        _surface = null;
        _surfaceBrush.Dispose();
        _effectBrush.Dispose();
        _visual.Dispose();
    }
}
