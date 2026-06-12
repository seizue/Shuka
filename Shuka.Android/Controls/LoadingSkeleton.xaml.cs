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
                    if (!_isAnimating) break;

                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        if (element.Parent != null && _isAnimating)
                        {
                            try
                            {
                                await element.FadeToAsync(0.3, 800, Easing.SinInOut);
                                if (_isAnimating && element.Parent != null)
                                {
                                    await element.FadeToAsync(1.0, 800, Easing.SinInOut);
                                }
                            }
                            catch { }
                        }
                    });
                    
                    await Task.Delay(150); // Stagger the shimmer effect
                }
                
                // Allow the current cycle of staggered animations to complete before restarting
                await Task.Delay(2000); 
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