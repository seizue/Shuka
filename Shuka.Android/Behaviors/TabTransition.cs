namespace Shuka.Android.Behaviors;

/// <summary>
/// Plays a directional slide-in animation on the page content when switching tabs.
/// The tab bar and header are NOT animated — only the content view slides.
///
/// Usage in OnAppearing:
///
///   protected override async void OnAppearing()
///   {
///       base.OnAppearing();
///       TabTransition.Prepare(BodyGrid, myTabIndex: N);
///       await TabTransition.SlideInAsync(BodyGrid);
///   }
///
/// Pass the specific content view (e.g. BodyGrid, BodyScrollView) — not the page itself.
/// </summary>
public static class TabTransition
{
    private static int _targetIndex     = 0;
    private static bool _goingRight    = true;
    private static bool _shouldAnimate = false;

    /// <summary>
    /// Updates the target index of the tab transition.
    /// Used to filter out intermediate tabs loaded sequentially by the renderer.
    /// </summary>
    public static void SetTargetIndex(int index)
    {
        _targetIndex = index;
    }

    /// <summary>
    /// Call synchronously at the top of OnAppearing (no await).
    /// Captures the slide direction and applies initial layout properties synchronously
    /// on the UI thread to prevent any momentary split-second flashing.
    /// </summary>
    public static void Prepare(View contentView, int myTabIndex)
    {
        // If myTabIndex is not the target index, we shouldn't animate it at all.
        // It is an intermediate tab being loaded sequentially by the renderer under the hood.
        if (myTabIndex != _targetIndex)
        {
            _shouldAnimate = false;
            contentView.Opacity = 0;
            contentView.TranslationX = 0;
            return;
        }

        int from = AppShell.LastTabIndex;
        int to   = myTabIndex;

        if (from == to)
        {
            _shouldAnimate = false;
            contentView.Opacity = 1.0;
            contentView.TranslationX = 0;
            contentView.Scale = 1.0;
            return;
        }

        _goingRight    = to > from;
        _shouldAnimate = true;

        // Apply starting states synchronously to prevent layout flashing
        contentView.Opacity      = 0;
        contentView.TranslationX = _goingRight ? 180 : -180; // Premium lateral slide distance
        contentView.Scale        = 1.0;                      // Keep scale static for a pure sliding transition
    }

    /// <summary>
    /// Animates the view into place with a premium fast-fade and smooth-slide.
    /// </summary>
    public static async Task SlideInAsync(View contentView)
    {
        if (!_shouldAnimate) return;

        // Wait one brief frame to allow the renderer to process the initial state
        await Task.Delay(16);

        // Native feel: fast fade (140ms) to feel solid, paired with a smooth cubic deceleration slide (220ms)
        await Task.WhenAll(
            contentView.TranslateToAsync(0, 0, 220, Easing.CubicOut),
            contentView.FadeToAsync(1.0, 140, Easing.Linear)
        );
    }
}
