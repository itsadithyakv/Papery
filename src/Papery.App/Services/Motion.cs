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
/// Everything is brief feedback within 120-260 ms, and nothing loops, so the app stays still while you read.
/// </remarks>
internal static class Motion
{
    private static readonly UISettings Settings = new();

    public static bool Enabled => Settings.AnimationsEnabled;

    /// <summary>A quick fade in, so a new screen arrives softly. Nothing moves, because movement across the whole window is what feels jarring.</summary>
    public static void FadeIn(UIElement element)
    {
        if (!Enabled)
        {
            return;
        }

        var visual = Prepare(element);
        var c = visual.Compositor;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        fade.Duration = TimeSpan.FromMilliseconds(120);
        visual.StartAnimation("Opacity", fade);
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
