using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace JRoute.Pages.Commuter;

public partial class Commuter : ContentPage
{
    public Commuter()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    // Make this async + nullable-friendly
    private async void OnBackButtonClicked(object? sender, EventArgs e)
    {
        // If you're using Shell, prefer: await Shell.Current.GoToAsync("..");
        if (Navigation?.NavigationStack?.Count > 1)
            await Navigation.PopAsync();
    }

    // Fix nullability: EventHandler expects object?
    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        try
        {
            var location = await Geolocation.Default.GetLastKnownLocationAsync()
                          ?? await Geolocation.Default.GetLocationAsync(
                               new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10)));

            if (location != null)
            {
                MyMap.MoveToRegion(MapSpan.FromCenterAndRadius(
                    new Location(location.Latitude, location.Longitude),
                    Distance.FromKilometers(2)));
            }
            else
            {
                await DisplayAlert("Location", "Unable to get location.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Unable to get location: {ex.Message}", "OK");
        }
    }
}
