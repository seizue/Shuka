namespace Shuka.Android.Pages;

public partial class AboutPage : ContentPage
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private async void OnBackTapped(object sender, TappedEventArgs e)
        => await Navigation.PopAsync();

    private async void OnGitHubTapped(object sender, TappedEventArgs e)
    {
        try { await Launcher.Default.OpenAsync(new Uri("https://github.com/seizue/Shuka")); }
        catch { await DisplayAlertAsync("Error", "Could not open browser.", "OK"); }
    }

    private async void OnBugTapped(object sender, TappedEventArgs e)
    {
        try { await Launcher.Default.OpenAsync(new Uri("https://github.com/seizue/Shuka/issues/new")); }
        catch { await DisplayAlertAsync("Error", "Could not open browser.", "OK"); }
    }
}
