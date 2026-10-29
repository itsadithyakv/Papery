using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Papery.Services;

/// <summary>
/// Papery's small, friendly animations, built on the Windows compositor (smooth, GPU, no extra library).
/// </summary>
/// <remarks>
/// Every animation checks Windows' "Animation effects" setting first and does nothing when it's off.
/// Interaction feedback stays within 150-250 ms; only the character's gentle idle float loops.
/// </remarks>
internal static class Motion
{
    private static readonly UISettings Settings = new();

    public static bool Enabled => Settings.AnimationsEnabled;

    /// <summary>Fades and slides an element up into place, optionally after a delay (for staggered lists).</summary>
    public static void Entrance(UIElement element, int delayMs = 0, float offsetY = 14)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = Prepare(element);
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var duration = TimeSpan.FromMilliseconds(240);
        var delay = TimeSpan.FromMilliseconds(delayMs);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(0, offsetY, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = duration;
        slide.DelayTime = delay;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    /// <summary>A slow, endless up-and-down float (the character's idle "breathing").</summary>
    public static void Float(UIElement element, float amplitude = 4, double seconds = 3.2)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = Prepare(element);
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
        var bob = c.CreateScalarKeyFrameAnimation();
        bob.InsertKeyFrame(0, 0);
        bob.InsertKeyFrame(0.5f, -amplitude, ease);
        bob.InsertKeyFrame(1, 0, ease);
        bob.Duration = TimeSpan.FromSeconds(seconds);
        bob.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation.Y", bob);
    }

    /// <summary>A quick happy bounce (scale up and settle), e.g. when a file is dragged over.</summary>
    public static void Pop(FrameworkElement element, float scale = 1.12f)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = Prepare(element);
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        var c = visual.Compositor;
        var pop = c.CreateVector3KeyFrameAnimation();
        pop.InsertKeyFrame(0, Vector3.One);
        pop.InsertKeyFrame(0.4f, new Vector3(scale, scale, 1));
        pop.InsertKeyFrame(1, Vector3.One);
        pop.Duration = TimeSpan.FromMilliseconds(260);
        visual.StartAnimation("Scale", pop);
    }

    /// <summary>A soft breathing opacity for "loading" placeholders.</summary>
    public static void Pulse(UIElement element)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = Prepare(element);
        var c = visual.Compositor;
        var pulse = c.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, 0.45f);
        pulse.InsertKeyFrame(0.5f, 0.85f);
        pulse.InsertKeyFrame(1, 0.45f);
        pulse.Duration = TimeSpan.FromMilliseconds(1300);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
    }

    /// <summary>Squashes an element vertically and back (used for blinking eyes).</summary>
    public static void Blink(UIElement element, Vector3 center)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = Prepare(element);
        visual.CenterPoint = center;
        var c = visual.Compositor;
        var blink = c.CreateVector3KeyFrameAnimation();
        blink.InsertKeyFrame(0, Vector3.One);
        blink.InsertKeyFrame(0.5f, new Vector3(1, 0.1f, 1));
        blink.InsertKeyFrame(1, Vector3.One);
        blink.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Scale", blink);
    }

    /// <summary>Slides an element vertically to an offset (used by toasts).</summary>
    public static void SlideTo(UIElement element, float y, float opacity, int ms = 220)
    {
        var visual = Prepare(element);
        if (!Enabled)
        {
            visual.Properties.InsertVector3("Translation", new Vector3(0, y, 0));
            visual.Opacity = opacity;
            return;
        }

        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1, new Vector3(0, y, 0), ease);
        move.Duration = TimeSpan.FromMilliseconds(ms);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity, ease);
        fade.Duration = TimeSpan.FromMilliseconds(ms);
        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Opacity", fade);
    }

    private static Visual Prepare(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }
}
