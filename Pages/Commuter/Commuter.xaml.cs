using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using System.Collections.ObjectModel;
using JRoute.Services;

namespace JRoute.Pages.Commuter;

public partial class Commuter : ContentPage
{
    private readonly ObservableCollection<PlaceResult> _suggestions = new();
    private readonly GooglePlacesService _placesService = new();
    private double _currentLatitude = 7.0731; // Default to Davao City
    private double _currentLongitude = 125.6128;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private Pin? _destinationPin;

    public Commuter()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        SuggestionsCollectionView.ItemsSource = _suggestions;
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
                _currentLatitude = location.Latitude;
                _currentLongitude = location.Longitude;
                
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

    private async Task RecenterToUserAsync(double kmRadius = 2)
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
                    Distance.FromKilometers(kmRadius)));
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

    private async void OnRecenterTapped(object? sender, TappedEventArgs e)
    {
        await BounceAsync(RecenterButton);
        await RecenterToUserAsync();
    }

    private async Task BounceAsync(VisualElement v)
    {
        if (v == null) return;
        try
        {
            await v.ScaleTo(0.92, 80, Easing.CubicOut);
            await v.ScaleTo(1.0, 120, Easing.CubicIn);
        }
        catch { }
    }

    private async void OnSearchFocused(object? sender, FocusEventArgs e)
    {
        await ShowPopularPlaces();
    }

    private void OnSearchUnfocused(object? sender, FocusEventArgs e)
    {
        // Delay hiding to allow tap on suggestions
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () =>
        {
            HideSuggestions();
        });
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        try
        {
            var searchText = e.NewTextValue?.Trim() ?? string.Empty;
            
            // Cancel previous search
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            
            if (string.IsNullOrEmpty(searchText))
            {
                await ShowPopularPlaces();
            }
            else
            {
                // Debounce search - wait 300ms before searching
                try
                {
                    await Task.Delay(300, _searchCancellationTokenSource.Token);
                    if (!_searchCancellationTokenSource.Token.IsCancellationRequested)
                    {
                        await SearchPlaces(searchText);
                    }
                }
                catch (TaskCanceledException)
                {
                    // Expected when cancellation is requested, ignore
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnSearchTextChanged: {ex.Message}");
        }
    }

    private void HideSuggestions()
    {
        SuggestionsDropdown.IsVisible = false;
    }

    private async Task ShowPopularPlaces()
    {
        try
        {
            // Search for popular places near current location
            var places = await _placesService.SearchPlacesAsync("restaurant mall school hospital", _currentLatitude, _currentLongitude);
            
            _suggestions.Clear();
            foreach (var place in places)
            {
                _suggestions.Add(place);
            }
            
            SuggestionsDropdown.IsVisible = places.Any();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing popular places: {ex.Message}");
            SuggestionsDropdown.IsVisible = false;
        }
    }

    private async Task SearchPlaces(string searchText)
    {
        try
        {
            var places = await _placesService.GetPlacePredictionsAsync(searchText, _currentLatitude, _currentLongitude);
            
            _suggestions.Clear();
            foreach (var place in places)
            {
                _suggestions.Add(place);
            }
            
            SuggestionsDropdown.IsVisible = places.Any();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error searching places: {ex.Message}");
            SuggestionsDropdown.IsVisible = false;
        }
    }

    private async void OnSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Grid grid && grid.BindingContext is PlaceResult place)
        {
            await SelectPlace(place);
        }
    }

    private async void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (e.CurrentSelection?.FirstOrDefault() is PlaceResult place)
            {
                await SelectPlace(place);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnSuggestionSelected: {ex.Message}");
        }
    }

    private async Task SelectPlace(PlaceResult place)
    {
        try
        {
            // Update search text
            SearchEntry.Text = place.Name;
            
            // Hide suggestions
            HideSuggestions();
            
            var location = new Location(place.Latitude, place.Longitude);
            
            // Check if a pin already exists for this location (within ~50 meters)
            var existingPin = MyMap.Pins.FirstOrDefault(pin => 
                Math.Abs(pin.Location.Latitude - place.Latitude) < 0.0005 && 
                Math.Abs(pin.Location.Longitude - place.Longitude) < 0.0005);
            
            if (existingPin != null)
            {
                // Focus on existing pin
                MyMap.MoveToRegion(MapSpan.FromCenterAndRadius(existingPin.Location, Distance.FromKilometers(1)));
            }
            else
            {
                // Remove previous destination pin if any
                if (_destinationPin != null)
                {
                    MyMap.Pins.Remove(_destinationPin);
                }
                
                // Create new red pin
                _destinationPin = new Pin
                {
                    Label = place.Name,
                    Address = place.Address,
                    Type = PinType.Place,
                    Location = location
                };
                
                MyMap.Pins.Add(_destinationPin);
                MyMap.MoveToRegion(MapSpan.FromCenterAndRadius(location, Distance.FromKilometers(1)));
            }
            
            // Unfocus the search entry
            SearchEntry.Unfocus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in SelectPlace: {ex.Message}");
        }
    }
}
