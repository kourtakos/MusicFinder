using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MusicFinder.App.Controls;

/// <summary>
/// A single line that scrolls smoothly, and only when it does not fit and <see cref="IsActive"/> is true. Short text just sits there. The animation
/// holds still while the pointer is over it (so it can be read), when it is not active, and when Windows animations are turned off. No timers, no
/// per-frame code of its own: one clocked animation on a transform at 30 fps.
/// </summary>
public sealed class MarqueeText : Decorator
{
    private const double Gap = 56;                 // empty space between the end of the text and its repeat
    private const double PixelsPerSecond = 38;
    private const double FadeWidth = 14;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText), new PropertyMetadata("", (d, _) => ((MarqueeText)d).Rebuild()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MarqueeText), new PropertyMetadata(false, (d, _) => ((MarqueeText)d).UpdateClock()));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(MarqueeText), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((MarqueeText)d).ApplyText()));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(MarqueeText), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((MarqueeText)d).Rebuild()));

    private readonly Canvas _canvas = new() { ClipToBounds = true, SnapsToDevicePixels = false };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _first = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _second = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform _move = new();
    private AnimationClock? _clock;
    private bool _overflow;
    private Brush? _fade;

    public MarqueeText()
    {
        _strip.RenderTransform = _move;
        _strip.Children.Add(_first);
        _strip.Children.Add(new Border { Width = Gap });
        _strip.Children.Add(_second);
        _canvas.Children.Add(_strip);
        Child = _canvas;
        SizeChanged += (_, _) => Rebuild();
        MouseEnter += (_, _) => UpdateClock();
        MouseLeave += (_, _) => UpdateClock();
        Unloaded += (_, _) => StopClock();
    }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    /// <summary>True when the text is wider than the space and therefore scrolls (when active). Exposed for tests.</summary>
    public bool IsOverflowing => _overflow;

    protected override Size MeasureOverride(Size constraint)
    {
        _canvas.Measure(new Size(double.PositiveInfinity, constraint.Height));
        var height = Math.Max(_first.DesiredSize.Height, 0);
        return new Size(double.IsInfinity(constraint.Width) ? 0 : constraint.Width, double.IsInfinity(constraint.Height) ? height : Math.Min(height, constraint.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _canvas.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    private void ApplyText()
    {
        foreach (var block in new[] { _first, _second })
        {
            block.Text = Text;
            block.Foreground = Foreground;
            block.FontSize = FontSize;
        }
    }

    private void Rebuild()
    {
        ApplyText();
        _first.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _first.DesiredSize.Width;
        var space = ActualWidth;
        _overflow = space > 0 && textWidth > space + 0.5;
        _second.Visibility = _overflow ? Visibility.Visible : Visibility.Collapsed;
        _fade = _overflow && space > FadeWidth * 3 ? FadeMask(space) : null;
        _canvas.Height = Math.Max(_first.DesiredSize.Height, 0);
        StopClock();
        _move.X = 0;
        if (_overflow && !ReducedMotion)
        {
            var distance = textWidth + Gap;
            var animation = new DoubleAnimation(0, -distance, new Duration(TimeSpan.FromSeconds(distance / PixelsPerSecond)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(animation, 30);
            _clock = animation.CreateClock();
            _clock.Controller!.Pause();
            _move.ApplyAnimationClock(TranslateTransform.XProperty, _clock);
        }
        UpdateClock();
        InvalidateMeasure();
    }

    private static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    private void UpdateClock()
    {
        if (_clock?.Controller is not { } controller) { _canvas.OpacityMask = null; return; }
        _canvas.OpacityMask = IsActive ? _fade : null;          // soft edges only while it moves, so resting text starts at full strength
        var run = IsActive && !IsMouseOver;
        if (run) controller.Resume(); else controller.Pause();
    }

    private void StopClock()
    {
        if (_clock is null) return;
        _move.ApplyAnimationClock(TranslateTransform.XProperty, null);
        _clock = null;
    }

    private static Brush FadeMask(double width)
    {
        var edge = FadeWidth / width;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, edge));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }
}
