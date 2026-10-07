using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Lyrider.TaskbarWidget;

public sealed class OutlinedLyricText : FrameworkElement
{
    private double _fontSize = 32;
    private TextAlignment _textAlignment = TextAlignment.Center;
    private Brush _foreground = Brushes.White;
    private FormattedText? _formatted;
    private Geometry? _geometry;
    private int[] _textElements = [];
    private Brush _highlight = Brushes.Transparent;

    public double KaraokeProgress { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public void SetText(string text, double fontSize, Color color, TextAlignment alignment = TextAlignment.Center)
    {
        if (Text == text && _fontSize == fontSize && _textAlignment == alignment &&
            _foreground is SolidColorBrush brush && brush.Color == color)
            return;
        Text = text;
        _textElements = StringInfo.ParseCombiningCharacters(text);
        _fontSize = fontSize;
        _textAlignment = alignment;
        _foreground = new SolidColorBrush(color);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetKaraokeProgress(double progress, Color color)
    {
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        if (KaraokeProgress == progress && _highlight is SolidColorBrush brush && brush.Color == color) return;
        KaraokeProgress = progress;
        _highlight = new SolidColorBrush(color);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 960;
        _formatted = new FormattedText(Text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            _fontSize, _foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = width,
            MaxTextHeight = _fontSize * 2.6,
            LineHeight = _fontSize * 1.25,
            TextAlignment = _textAlignment,
            Trimming = TextTrimming.CharacterEllipsis
        };
        _geometry = _formatted.BuildGeometry(new Point(0, 2));
        return new Size(width, string.IsNullOrEmpty(Text) ? 0 : Math.Min(_formatted.Height + 4, _fontSize * 2.6));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_formatted is not null && _geometry is not null && !string.IsNullOrEmpty(Text))
        {
            drawingContext.DrawGeometry(null, new Pen(Brushes.Black, 4), _geometry);
            drawingContext.DrawGeometry(_foreground, null, _geometry);
            if (KaraokeProgress <= 0 || _textElements.Length == 0) return;
            if (KaraokeProgress >= 1)
            {
                drawingContext.DrawGeometry(_highlight, null, _geometry);
                return;
            }

            // 按文本元素推进，换行后继续扫色，组合字符和 emoji 保持完整。
            var advanced = KaraokeProgress * _textElements.Length;
            var element = (int)advanced;
            var start = _textElements[element];
            var end = element + 1 < _textElements.Length ? _textElements[element + 1] : Text.Length;
            var clip = new GeometryGroup();
            if (start > 0 && _formatted.BuildHighlightGeometry(new Point(0, 2), 0, start) is { } completed)
                clip.Children.Add(completed);
            if (_formatted.BuildHighlightGeometry(new Point(0, 2), start, end - start) is { } current)
            {
                var bounds = current.Bounds;
                if (!bounds.IsEmpty)
                {
                    bounds.Width *= advanced - element;
                    clip.Children.Add(new RectangleGeometry(bounds));
                }
            }
            drawingContext.PushClip(clip);
            drawingContext.DrawGeometry(_highlight, null, _geometry);
            drawingContext.Pop();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LyricTextAutomationPeer(this);

    private sealed class LyricTextAutomationPeer(OutlinedLyricText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Text;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
