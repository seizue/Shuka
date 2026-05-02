namespace Shuka.Android.Controls;

public partial class LoadingOverlay : ContentView
{
    private bool _isAnimating = false;

    public LoadingOverlay()
    {
        InitializeComponent();
    }

    public async Task ShowAsync(string title = "Loading...", string subtitle = "Please wait")
    {
        LoadingTitle.Text = title;
        LoadingSubtitle.Text = subtitle;
        
        // Start with overlay hidden
        Opacity = 0;
        Scale = 0.8;
        IsVisible = true;
        
        // Animate in
        await Task.WhenAll(
            this.FadeToAsync(1.0, 300, Easing.CubicOut),
            this.ScaleToAsync(1.0, 300, Easing.CubicOut)
        );
        
        // Start loading icon animation
        StartLoadingAnimation();
    }

    public async Task HideAsync()
    {
        StopLoadingAnimation();
        
        // Animate out
        await Task.WhenAll(
            this.FadeToAsync(0, 250, Easing.CubicIn),
            this.ScaleToAsync(0.9, 250, Easing.CubicIn)
        );
        
        IsVisible = false;
    }

    private void StartLoadingAnimation()
    {
        _isAnimating = true;
        
        _ = Task.Run(async () =>
        {
            while (_isAnimating)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (LoadingIcon.Parent != null)
                    {
                        await LoadingIcon.RotateToAsync(360, 1000, Easing.Linear);
                        LoadingIcon.Rotation = 0;
                    }
                });
            }
        });
    }

    private void StopLoadingAnimation()
    {
        _isAnimating = false;
    }

    public void UpdateProgress(string title, string subtitle = "")
    {
        LoadingTitle.Text = title;
        if (!string.IsNullOrEmpty(subtitle))
            LoadingSubtitle.Text = subtitle;
    }
}