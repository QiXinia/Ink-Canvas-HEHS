using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace InkCanvas.Controls.Animation;

/// <summary>
/// Shared motion helpers.
/// <para>
/// All motion goes through here so it can be disabled in one place: when the user turns off
/// animations in Windows (Accessibility &gt; Visual effects), every duration collapses to zero
/// instead of each control deciding for itself.
/// </para>
/// </summary>
public static class Motion
{
    private static readonly Lazy<Windows.UI.ViewManagement.UISettings> Settings = new(() => new());

    /// <summary>Read the current system preference so disabling animations takes effect immediately.</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try { return Settings.Value.AnimationsEnabled; }
            catch { return true; }
        }
    }

    public static TimeSpan Fast => AnimationsEnabled ? TimeSpan.FromMilliseconds(140) : TimeSpan.Zero;
    public static TimeSpan Normal => AnimationsEnabled ? TimeSpan.FromMilliseconds(240) : TimeSpan.Zero;
    public static TimeSpan Slow => AnimationsEnabled ? TimeSpan.FromMilliseconds(380) : TimeSpan.Zero;

    /// <summary>
    /// Attach an implicit scale animation so press/hover feedback is driven by Composition
    /// rather than a per-frame storyboard.
    /// </summary>
    public static void EnableImplicitScale(UIElement element)
    {
        if (element is not FrameworkElement fe) return;
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0);

        if (!AnimationsEnabled) return;

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, Vector3.One);
        animation.Duration = Normal;
        animation.Target = "Scale";

        var group = compositor.CreateAnimationGroup();
        group.Add(animation);
        visual.ImplicitAnimations = compositor.CreateImplicitAnimationCollection();
        visual.ImplicitAnimations["Scale"] = group;
    }

    /// <summary>Fade + rise entrance, used when a panel or window content appears.</summary>
    public static void PlayEntrance(UIElement element, float offsetPx = 12f) => PlayEntrance(element, offsetPx, 0);

    public static void PlayEntrance(UIElement element, float offsetPx, int delayMilliseconds)
    {
        if (element is null || !AnimationsEnabled) return;
        // Offset belongs to XAML layout. Animating it to zero discards the element's arranged
        // position, moving centred/inset content outside its window. Translation is additive.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = Normal;
        fade.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMilliseconds));

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0f, new Vector3(0, offsetPx, 0));
        rise.InsertKeyFrame(1f, Vector3.Zero);
        rise.Duration = Normal;
        rise.DelayTime = fade.DelayTime;
        rise.Target = "Translation";

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", rise);
    }

    /// <summary>Spring-like scale pulse for selection changes on the floating bar.</summary>
    public static void Pulse(UIElement element, float to = 1.08f)
    {
        if (element is null || !AnimationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        if (element is FrameworkElement fe)
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);

        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(to, to, 1);
        spring.DampingRatio = 0.55f;
        spring.Period = TimeSpan.FromMilliseconds(60);
        visual.StartAnimation("Scale", spring);

        var back = compositor.CreateSpringVector3Animation();
        back.FinalValue = Vector3.One;
        back.DampingRatio = 0.7f;
        back.Period = TimeSpan.FromMilliseconds(80);
        back.DelayTime = TimeSpan.FromMilliseconds(120);
        visual.StartAnimation("Scale", back);
    }
}
