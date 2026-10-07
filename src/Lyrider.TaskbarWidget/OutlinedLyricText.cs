using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
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
    private DesktopLyricsTextDirection _direction;
    private DesktopLyricsFontWeight _weight = DesktopLyricsFontWeight.SemiBold;
    private double _strokeThickness = 4;
    private Brush _stroke = Brushes.Black;
    private readonly List<Rect> _verticalElements = [];

    public void ApplyStyle(DesktopLyricsOptions options)
    {
        var stroke = (Color)ColorConverter.ConvertFromString(options.StrokeColor);
        if (_direction == options.TextDirection && _weight == options.FontWeight &&
            _strokeThickness == options.StrokeThickness && _stroke is SolidColorBrush brush && brush.Color == stroke) return;
        _direction = options.TextDirection;
        _weight = options.FontWeight;
        _strokeThickness = options.StrokeThickness;
        _stroke = new SolidColorBrush(stroke);
        InvalidateMeasure();
        InvalidateVisual();
    }

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
        _verticalElements.Clear();
        if (_direction == DesktopLyricsTextDirection.Vertical) return MeasureVertical(availableSize);
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 960;
        _formatted = new FormattedText(Text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            Typeface(),
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

    private Typeface Typeface() => new(new FontFamily("Segoe UI"), FontStyles.Normal, _weight switch
    {
        DesktopLyricsFontWeight.Normal => FontWeights.Normal,
        DesktopLyricsFontWeight.Bold => FontWeights.Bold,
        _ => FontWeights.SemiBold
    }, FontStretches.Normal);

    private FormattedText Format(string text) => new(text, CultureInfo.CurrentUICulture,
        System.Windows.FlowDirection.LeftToRight, Typeface(), _fontSize, _foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Size MeasureVertical(Size availableSize)
    {
        _formatted = null;
        var geometries = new GeometryGroup();
        var limit = double.IsFinite(availableSize.Height) ? Math.Max(1, availableSize.Height) : 440;
        var cell = _fontSize * 1.25;
        var width = cell + _strokeThickness;
        var y = _strokeThickness / 2 + 2;
        var ellipsis = Format("⋮");
        var ellipsisHeight = Math.Max(cell, ellipsis.Height);
        var padding = _strokeThickness / 2 + 2;
        var truncated = false;
        for (var element = 0; element < _textElements.Length;)
        {
            var first = element;
            var end = element + 1;
            string Element(int index) => Text[_textElements[index]..(index + 1 < _textElements.Length ? _textElements[index + 1] : Text.Length)];
            var rotated = IsLatinOrDigit(Element(element));
            if (rotated)
                while (end < _textElements.Length && IsLatinOrDigit(Element(end))) end++;
            var text = Text[_textElements[first]..(end < _textElements.Length ? _textElements[end] : Text.Length)];
            var formatted = Format(text);
            var extent = rotated ? formatted.WidthIncludingTrailingWhitespace : Math.Max(cell, formatted.Height);
            var reserve = end < _textElements.Length ? ellipsisHeight + padding : padding;
            while (rotated && end > first && y + extent + reserve > limit)
            {
                end--;
                text = Text[_textElements[first].._textElements[end]];
                formatted = Format(text);
                extent = formatted.WidthIncludingTrailingWhitespace;
                reserve = ellipsisHeight + padding;
            }
            if (end == first || y + extent + reserve > limit)
            {
                truncated = true;
                break;
            }
            var matrix = rotated
                ? new Matrix(0, 1, -1, 0, (width + formatted.Height) / 2, y)
                : new Matrix(1, 0, 0, 1, (width - formatted.WidthIncludingTrailingWhitespace) / 2, y);
            var geometry = formatted.BuildGeometry(new Point(0, 0)).Clone();
            geometry.Transform = new MatrixTransform(matrix);
            geometries.Children.Add(geometry);
            for (var index = first; index < end; index++)
            {
                var start = _textElements[index] - _textElements[first];
                var length = Element(index).Length;
                var bounds = formatted.BuildHighlightGeometry(new Point(0, 0), start, length)?.Bounds ?? Rect.Empty;
                if (!bounds.IsEmpty) bounds.Transform(matrix);
                _verticalElements.Add(bounds);
            }
            y += extent;
            element = end;
            if (end < _textElements.Length && rotated && !IsLatinOrDigit(Element(end))) continue;
            if (rotated && end < _textElements.Length && IsLatinOrDigit(Element(end)))
            {
                truncated = true;
                break;
            }
        }
        if (truncated && y + ellipsisHeight <= limit)
        {
            geometries.Children.Add(ellipsis.BuildGeometry(new Point((width - ellipsis.Width) / 2, y)));
            y += ellipsisHeight;
        }
        _geometry = geometries;
        return new Size(width, Text.Length == 0 ? 0 : Math.Min(limit, y + _strokeThickness / 2 + 2));
    }

    private static bool IsLatinOrDigit(string element) => element.Length > 0 &&
        ((char.IsLetter(element[0]) && element[0] <= '\u024f') || char.IsDigit(element[0]));

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometry is not null && !string.IsNullOrEmpty(Text))
        {
            if (_strokeThickness > 0) drawingContext.DrawGeometry(null, new Pen(_stroke, _strokeThickness), _geometry);
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
            if (_direction == DesktopLyricsTextDirection.Vertical)
            {
                for (var index = 0; index < _verticalElements.Count && index <= element; index++)
                {
                    var bounds = _verticalElements[index];
                    if (bounds.IsEmpty) continue;
                    if (index == element) bounds.Height *= advanced - element;
                    clip.Children.Add(new RectangleGeometry(bounds));
                }
            }
            else if (_formatted is not null)
            {
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
