using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace Lyrider.Services;

/// <summary>
/// Composition-layer motion for the main window. Everything animates the element's
/// handoff visual (Opacity, Translation, Scale), so callers must not also use the XAML
/// Translation/Scale facades on the same element.
/// </summary>
internal static class UiMotion
{
    private const string Translation = "Translation";
    private static readonly UISettings SystemSettings = new();

    /// <summary>Honors "Animation effects" in Windows accessibility settings.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                return SystemSettings.AnimationsEnabled;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>Fades the element in while it settles from an offset (and scale) to rest.</summary>
    public static Task EnterAsync(
        UIElement element,
        Vector2 fromOffset,
        int durationMilliseconds = 320,
        int delayMilliseconds = 0,
        float fromOpacity = 0,
        float fromScale = 1)
    {
        var visual = PrepareVisual(element);
        if (!IsEnabled)
        {
            Reset(element);
            return Task.CompletedTask;
        }

        var compositor = visual.Compositor;
        var easing = Decelerate(compositor);
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, fromOpacity);
        opacity.InsertKeyFrame(1, 1, easing);
        Time(opacity, duration, delay);
        visual.StartAnimation(nameof(Visual.Opacity), opacity);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(fromOffset, 0));
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        Time(translation, duration, delay);
        visual.StartAnimation(Translation, translation);

        if (fromScale != 1)
        {
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1));
            scale.InsertKeyFrame(1, Vector3.One, easing);
            Time(scale, duration, delay);
            visual.StartAnimation(nameof(Visual.Scale), scale);
        }

        return EndAsync(batch);
    }

    /// <summary>Plays <see cref="EnterAsync"/> on each element with a growing delay.</summary>
    public static void Stagger(
        IEnumerable<UIElement> elements,
        Vector2 fromOffset,
        int stepMilliseconds = 40,
        int maximumDelayMilliseconds = 240,
        int durationMilliseconds = 360)
    {
        var delay = 0;
        foreach (var element in elements)
        {
            _ = EnterAsync(element, fromOffset, durationMilliseconds, delay);
            delay = Math.Min(maximumDelayMilliseconds, delay + stepMilliseconds);
        }
    }

    /// <summary>Fades the element out towards an offset. Call <see cref="Reset"/> after hiding it.</summary>
    public static Task ExitAsync(UIElement element, Vector2 toOffset, int durationMilliseconds = 160)
    {
        var visual = PrepareVisual(element);
        if (!IsEnabled)
        {
            return Task.CompletedTask;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0), new Vector2(1, 0.5f));
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, 0, easing);
        opacity.Duration = duration;
        visual.StartAnimation(nameof(Visual.Opacity), opacity);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(1, new Vector3(toOffset, 0), easing);
        translation.Duration = duration;
        visual.StartAnimation(Translation, translation);

        return EndAsync(batch);
    }

    /// <summary>Returns the visual to its resting state without animating.</summary>
    public static void Reset(UIElement element)
    {
        var visual = PrepareVisual(element);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(Translation);
        visual.StopAnimation(nameof(Visual.Scale));
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3(Translation, Vector3.Zero);
    }

    /// <summary>A short springy scale from <paramref name="fromScale"/> back to 1.</summary>
    public static void Pop(UIElement element, float fromScale = 0.8f)
    {
        if (!IsEnabled)
        {
            return;
        }

        var visual = PrepareVisual(element);
        var compositor = visual.Compositor;
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1));
        scale.InsertKeyFrame(1, Vector3.One, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.34f, 1.56f),
            new Vector2(0.64f, 1)));
        scale.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation(nameof(Visual.Scale), scale);
    }

    /// <summary>Moves the element horizontally to <paramref name="x"/> with a spring.</summary>
    public static void SlideTo(UIElement element, float x, bool animate)
    {
        var visual = PrepareVisual(element);
        var target = new Vector3(x, 0, 0);
        if (!animate || !IsEnabled)
        {
            visual.StopAnimation(Translation);
            visual.Properties.InsertVector3(Translation, target);
            return;
        }

        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.DampingRatio = 0.72f;
        spring.Period = TimeSpan.FromMilliseconds(55);
        spring.FinalValue = target;
        visual.StartAnimation(Translation, spring);
    }

    /// <summary>Grows the element slightly on hover and presses it in on click.</summary>
    public static void AttachPointerScale(UIElement element, float hoverScale = 1.06f, float pressedScale = 0.92f)
    {
        var hovered = false;
        void Animate(float value)
        {
            if (!IsEnabled)
            {
                return;
            }

            var visual = PrepareVisual(element);
            var spring = visual.Compositor.CreateSpringVector3Animation();
            spring.DampingRatio = 0.6f;
            spring.Period = TimeSpan.FromMilliseconds(40);
            spring.FinalValue = new Vector3(value, value, 1);
            visual.StartAnimation(nameof(Visual.Scale), spring);
        }

        // Buttons mark pointer presses as handled, so listen to handled events too.
        element.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) =>
        {
            hovered = true;
            Animate(hoverScale);
        }), true);
        element.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) =>
        {
            hovered = false;
            Animate(1);
        }), true);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Animate(pressedScale)), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Animate(hovered ? hoverScale : 1)), true);
        element.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Animate(hovered ? hoverScale : 1)), true);
    }

    private static Visual PrepareVisual(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        // Scale around the element's current center; layout may have resized it since last time.
        visual.CenterPoint = new Vector3(element.ActualSize * 0.5f, 0);
        return visual;
    }

    private static CompositionEasingFunction Decelerate(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1));

    private static void Time(KeyFrameAnimation animation, TimeSpan duration, TimeSpan delay)
    {
        animation.Duration = duration;
        animation.DelayTime = delay;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
    }

    private static Task EndAsync(CompositionScopedBatch batch)
    {
        var completion = new TaskCompletionSource();
        batch.Completed += (_, _) => completion.TrySetResult();
        batch.End();
        return completion.Task;
    }
}
