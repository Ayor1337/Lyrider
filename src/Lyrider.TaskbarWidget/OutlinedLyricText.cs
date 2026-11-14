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
    private Pen? _pen = Frozen(new Pen(Brushes.Black, 4));
    private string? _fontFamily;
    private Typeface _typeface = CreateTypeface(null, DesktopLyricsFontWeight.SemiBold);
    private CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly List<Rect> _verticalElements = [];

    public const string DefaultFontFamily = DesktopLyricsFonts.DefaultFamily;

    public void ApplyStyle(DesktopLyricsOptions options)
    {
        var stroke = (Color)ColorConverter.ConvertFromString(options.StrokeColor);
        if (_direction == options.TextDirection && _weight == options.FontWeight && _fontFamily == options.FontFamily &&
            _strokeThickness == options.StrokeThickness && _stroke is SolidColorBrush brush && brush.Color == stroke) return;
        _direction = options.TextDirection;
        _weight = options.FontWeight;
        _fontFamily = options.FontFamily;
        _typeface = CreateTypeface(_fontFamily, _weight);
        _strokeThickness = options.StrokeThickness;
        _stroke = Frozen(new SolidColorBrush(stroke));
        _pen = _strokeThickness > 0 ? Frozen(new Pen(_stroke, _strokeThickness)) : null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    public CultureInfo Culture => _culture;

    public double KaraokeProgress { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public void SetText(string text, double fontSize, Color color, TextAlignment alignment = TextAlignment.Center,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        if (Text == text && _fontSize == fontSize && _textAlignment == alignment && Equals(_culture, culture) &&
            _foreground is SolidColorBrush brush && brush.Color == color)
            return;
        Text = text;
        _textElements = StringInfo.ParseCombiningCharacters(text);
        _fontSize = fontSize;
        _textAlignment = alignment;
        _culture = culture;
        _foreground = Frozen(new SolidColorBrush(color));
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetKaraokeProgress(double progress, Color color)
    {
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        var sameColor = _highlight is SolidColorBrush brush && brush.Color == color;
        if (KaraokeProgress == progress && sameColor) return;
        KaraokeProgress = progress;
        if (!sameColor) _highlight = Frozen(new SolidColorBrush(color));
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _verticalElements.Clear();
        if (_direction == DesktopLyricsTextDirection.Vertical) return MeasureVertical(availableSize);
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 960;
        _formatted = new FormattedText(Text, _culture, System.Windows.FlowDirection.LeftToRight,
            _typeface,
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

    // 自选字体缺字时依次回退到 Segoe UI 和系统复合字体，中日文字形由渲染语言决定。
    private static Typeface CreateTypeface(string? family, DesktopLyricsFontWeight weight) => new(
        new FontFamily(string.IsNullOrWhiteSpace(family) ? DefaultFontFamily : $"{family}, {DefaultFontFamily}"),
        FontStyles.Normal, weight switch
        {
            DesktopLyricsFontWeight.Normal => FontWeights.Normal,
            DesktopLyricsFontWeight.Bold => FontWeights.Bold,
            _ => FontWeights.SemiBold
        }, FontStretches.Normal);

    private FormattedText Format(string text) => new(text, _culture,
        System.Windows.FlowDirection.LeftToRight, _typeface, _fontSize, _foreground,
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
            var rotated = IsRotatedInVertical(Element(element));
            if (rotated)
                while (end < _textElements.Length && IsRotatedInVertical(Element(end))) end++;
            var text = Text[_textElements[first]..(end < _textElements.Length ? _textElements[end] : Text.Length)];
            if (!rotated) text = ToVerticalForm(text);
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
            if (end < _textElements.Length && rotated && !IsRotatedInVertical(Element(end))) continue;
            if (rotated && end < _textElements.Length && IsRotatedInVertical(Element(end)))
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

    // \u7ad6\u6392\u65f6\u6a2a\u5411\u5ef6\u4f38\u7684\u7b26\u53f7\u968f\u62c9\u4e01\u6587\u5b57\u4e00\u8d77\u65cb\u8f6c 90\u00b0\u3002
    private static bool IsRotatedInVertical(string element) => IsLatinOrDigit(element) ||
        element is "\u30fc" or "\uff5e" or "\u301c" or "~" or "-" or "\u2010" or "\u2013" or "\uff0d" or "\uff1d" or "=";

    // \u6709\u7ad6\u6392\u4e13\u7528\u5b57\u5f62\u7684\u4e2d\u65e5\u6587\u6807\u70b9\u6362\u6210\u5bf9\u5e94\u5b57\u5f62\uff1a\u53e5\u8bfb\u53f7\u843d\u5728\u5b57\u683c\u53f3\u4e0a\u89d2\uff0c\u62ec\u53f7\u8f6c\u4e3a\u7ad6\u5411\u3002
    private static readonly Dictionary<char, char> VerticalForms = new()
    {
        ['\uff0c'] = '\ufe10', [','] = '\ufe10', ['\u3001'] = '\ufe11', ['\u3002'] = '\ufe12', ['\uff1a'] = '\ufe13', ['\uff1b'] = '\ufe14',
        ['\uff01'] = '\ufe15', ['\uff1f'] = '\ufe16', ['\u3016'] = '\ufe17', ['\u3017'] = '\ufe18', ['\u2026'] = '\ufe19', ['\u2025'] = '\ufe30',
        ['\u2014'] = '\ufe31', ['\uff3f'] = '\ufe33', ['\uff08'] = '\ufe35', ['\uff09'] = '\ufe36', ['('] = '\ufe35', [')'] = '\ufe36',
        ['\uff5b'] = '\ufe37', ['\uff5d'] = '\ufe38', ['\u3014'] = '\ufe39', ['\u3015'] = '\ufe3a', ['\u3010'] = '\ufe3b', ['\u3011'] = '\ufe3c',
        ['\u300a'] = '\ufe3d', ['\u300b'] = '\ufe3e', ['\u3008'] = '\ufe3f', ['\u3009'] = '\ufe40', ['\u300c'] = '\ufe41', ['\u300d'] = '\ufe42',
        ['\u300e'] = '\ufe43', ['\u300f'] = '\ufe44', ['\uff3b'] = '\ufe47', ['\uff3d'] = '\ufe48', ['['] = '\ufe47', [']'] = '\ufe48'
    };

    public static string ToVerticalForm(string element) =>
        element.Length == 1 && VerticalForms.TryGetValue(element[0], out var vertical) ? vertical.ToString() : element;

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometry is not null && !string.IsNullOrEmpty(Text))
        {
            if (_pen is not null) drawingContext.DrawGeometry(null, _pen, _geometry);
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
