namespace Shuka.Android.Controls;

public partial class LoadingSkeleton : ContentView
{
    private bool _isAnimating = false;

    public LoadingSkeleton()
    {
        InitializeComponent();
    }

    protected override async void OnParentSet()
    {
        base.OnParentSet();
        
        if (Parent != null && !_isAnimating)
        {
            await StartShimmerAnimation();
        }
    }

    private async Task StartShimmerAnimation()
    {
        _isAnimating = true;
        
        // Get all skeleton elements
        var skeletonElements = GetSkeletonElements(SkeletonContainer);
        
        // Start continuous shimmer animation
        _ = Task.Run(async () =>
        {
            while (_isAnimating && Parent != null)
            {
                foreach (var element in skeletonElements)
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        if (element.Parent != null)
                        {
                            await element.FadeToAsync(0.3, 800, Easing.SinInOut);
                            await element.FadeToAsync(1.0, 800, Easing.SinInOut);
                        }
                    });
                    
                    await Task.Delay(100); // Stagger the shimmer effect
                }
                
                await Task.Delay(200); // Pause between cycles
            }
        });
    }

    private List<Border> GetSkeletonElements(Layout layout)
    {
        var elements = new List<Border>();
        
        foreach (var child in layout.Children)
        {
            if (child is Border border)
            {
                elements.Add(border);
            }
            else if (child is Layout childLayout)
            {
                elements.AddRange(GetSkeletonElements(childLayout));
            }
        }
        
        return elements;
    }

    public void StopAnimation()
    {
        _isAnimating = false;
    }

    public async Task FadeOut()
    {
        StopAnimation();
        await this.FadeToAsync(0, 300, Easing.CubicIn);
        IsVisible = false;
    }
}