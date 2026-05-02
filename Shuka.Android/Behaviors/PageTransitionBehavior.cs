using Microsoft.Maui.Controls;

namespace Shuka.Android.Behaviors;

public class PageTransitionBehavior : Behavior<ContentPage>
{
    public static readonly BindableProperty TransitionTypeProperty =
        BindableProperty.Create(nameof(TransitionType), typeof(PageTransitionType), typeof(PageTransitionBehavior), PageTransitionType.SlideFromRight);

    public PageTransitionType TransitionType
    {
        get => (PageTransitionType)GetValue(TransitionTypeProperty);
        set => SetValue(TransitionTypeProperty, value);
    }

    protected override void OnAttachedTo(ContentPage bindable)
    {
        base.OnAttachedTo(bindable);
        bindable.Appearing += OnPageAppearing;
        bindable.Disappearing += OnPageDisappearing;
    }

    protected override void OnDetachingFrom(ContentPage bindable)
    {
        base.OnDetachingFrom(bindable);
        bindable.Appearing -= OnPageAppearing;
        bindable.Disappearing -= OnPageDisappearing;
    }

    private async void OnPageAppearing(object? sender, EventArgs e)
    {
        if (sender is not ContentPage page) return;

        await AnimatePageIn(page);
    }

    private async void OnPageDisappearing(object? sender, EventArgs e)
    {
        if (sender is not ContentPage page) return;

        await AnimatePageOut(page);
    }

    private async Task AnimatePageIn(ContentPage page)
    {
        switch (TransitionType)
        {
            case PageTransitionType.SlideFromRight:
                page.TranslationX = 50;
                page.Opacity = 0;
                await Task.WhenAll(
                    page.TranslateToAsync(0, 0, 350, Easing.CubicOut),
                    page.FadeToAsync(1, 350, Easing.CubicOut)
                );
                break;

            case PageTransitionType.SlideFromBottom:
                page.TranslationY = 30;
                page.Opacity = 0;
                await Task.WhenAll(
                    page.TranslateToAsync(0, 0, 400, Easing.CubicOut),
                    page.FadeToAsync(1, 400, Easing.CubicOut)
                );
                break;

            case PageTransitionType.FadeIn:
                page.Opacity = 0;
                page.Scale = 0.95;
                await Task.WhenAll(
                    page.FadeToAsync(1, 300, Easing.CubicOut),
                    page.ScaleToAsync(1, 300, Easing.CubicOut)
                );
                break;

            case PageTransitionType.ZoomIn:
                page.Scale = 0.8;
                page.Opacity = 0;
                await Task.WhenAll(
                    page.ScaleToAsync(1, 400, Easing.CubicOut),
                    page.FadeToAsync(1, 400, Easing.CubicOut)
                );
                break;
        }
    }

    private async Task AnimatePageOut(ContentPage page)
    {
        switch (TransitionType)
        {
            case PageTransitionType.SlideFromRight:
                await Task.WhenAll(
                    page.TranslateToAsync(-30, 0, 250, Easing.CubicIn),
                    page.FadeToAsync(0.7, 250, Easing.CubicIn)
                );
                break;

            case PageTransitionType.SlideFromBottom:
                await Task.WhenAll(
                    page.TranslateToAsync(0, 20, 250, Easing.CubicIn),
                    page.FadeToAsync(0.7, 250, Easing.CubicIn)
                );
                break;

            case PageTransitionType.FadeIn:
                await Task.WhenAll(
                    page.FadeToAsync(0.7, 200, Easing.CubicIn),
                    page.ScaleToAsync(0.98, 200, Easing.CubicIn)
                );
                break;

            case PageTransitionType.ZoomIn:
                await Task.WhenAll(
                    page.ScaleToAsync(0.95, 200, Easing.CubicIn),
                    page.FadeToAsync(0.7, 200, Easing.CubicIn)
                );
                break;
        }
    }
}

public enum PageTransitionType
{
    SlideFromRight,
    SlideFromBottom,
    FadeIn,
    ZoomIn
}