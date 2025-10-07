using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using System.Collections.ObjectModel;
using JRoute.Services;
using JRoute.Models;

namespace JRoute.Pages.Commuter;

public partial class Commuter : ContentPage
{
    private readonly ObservableCollection<PlaceResult> _suggestions = new();
    private readonly GooglePlacesService _placesService = new();
    private RouteService? _routeService;
    private RouteVisualizationService? _routeVisualizationService;
    private double _currentLatitude = 7.0731; // Default to Davao City
    private double _currentLongitude = 125.6128;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private Pin? _destinationPin;
    private ComputedRoute? _currentRoute;
    private bool _isBottomSheetExpanded = false; // Start collapsed
    private bool _hasDestination = false;

    public Commuter()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        SuggestionsCollectionView.ItemsSource = _suggestions;
        
        // Set up size change listeners for dynamic positioning
        BottomSheet.SizeChanged += (_, __) => UpdateRecenterPosition();
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
            // Initialize route services
            var geoProvider = Handler?.MauiContext?.Services.GetService<IGeoJsonProvider>();
            if (geoProvider != null)
            {
                _routeService = new RouteService(geoProvider);
                _routeVisualizationService = new RouteVisualizationService(MyMap);
            }

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

            // Initialize bottom sheet in collapsed state
            await InitializeBottomSheet();
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
                // Focus on existing pin and calculate route
                await CalculateAndDisplayRoute(location);
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
                
                // Set destination state
                _hasDestination = true;
                UpdateGrabberAppearance();
                
                // Calculate and display route
                await CalculateAndDisplayRoute(location);
            }
            
            // Unfocus the search entry
            SearchEntry.Unfocus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in SelectPlace: {ex.Message}");
        }
    }

    private async Task CalculateAndDisplayRoute(Location destination)
    {
        try
        {
            if (_routeService == null || _routeVisualizationService == null)
            {
                await DisplayAlert("Error", "Route service not initialized", "OK");
                return;
            }

            // Clear previous routes
            _routeVisualizationService.ClearRoutes();

            // Show loading indicator (you can add a loading overlay here)
            var userLocation = new Location(_currentLatitude, _currentLongitude);
            
            System.Diagnostics.Debug.WriteLine($"=== ROUTE CALCULATION START ===");
            System.Diagnostics.Debug.WriteLine($"From: {userLocation.Latitude:F6}, {userLocation.Longitude:F6}");
            System.Diagnostics.Debug.WriteLine($"To: {destination.Latitude:F6}, {destination.Longitude:F6}");
            System.Diagnostics.Debug.WriteLine($"Direct distance: {Location.CalculateDistance(userLocation, destination, DistanceUnits.Kilometers):F2} km");
            
            // Calculate optimal route
            _currentRoute = await _routeService.FindOptimalRouteAsync(userLocation, destination);
            
            if (_currentRoute != null)
            {
                // Display the route on map
                _routeVisualizationService.DisplayComputedRoute(_currentRoute, userLocation);
                
                // Focus map on the entire route
                _routeVisualizationService.FocusOnRoute(_currentRoute, userLocation);
                
                // Update bottom sheet with route info
                UpdateBottomSheetInfo();
                
                // Expand bottom sheet to show route information
                if (!_isBottomSheetExpanded)
                {
                    await ExpandBottomSheet();
                }
                
                // Show route summary
                await ShowRouteSummary(_currentRoute);
            }
            else
            {
                await DisplayAlert("No Route Found", "Unable to find a suitable route to your destination.", "OK");
                // Just focus on destination
                MyMap.MoveToRegion(MapSpan.FromCenterAndRadius(destination, Distance.FromKilometers(1)));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error calculating route: {ex.Message}");
            await DisplayAlert("Route Error", "Unable to calculate route. Please try again.", "OK");
        }
    }

    private async Task ShowRouteSummary(ComputedRoute route)
    {
        var summary = new System.Text.StringBuilder();
        
        if (route.Segments.Any())
        {
            summary.AppendLine($"🚌 Route Summary:");
            summary.AppendLine($"• Total Distance: {route.TotalDistance:F1}m");
            summary.AppendLine($"• Estimated Time: {route.TotalDuration.TotalMinutes:F0} minutes");
            summary.AppendLine($"• Total Fare: ₱{route.TotalFare:F2}");
            summary.AppendLine($"• Transfers: {route.TransferCount}");
            summary.AppendLine();
            
            for (int i = 0; i < route.Segments.Count; i++)
            {
                var segment = route.Segments[i];
                summary.AppendLine($"Step {i + 1}: Take {segment.Route.Name}");
                summary.AppendLine($"  📍 Board at: {segment.StartPoint.Name}");
                summary.AppendLine($"  📍 Get off at: {segment.EndPoint.Name}");
                summary.AppendLine($"  💰 Fare: ₱{segment.Fare:F2}");
                summary.AppendLine();
            }
        }
        else
        {
            summary.AppendLine($"🚶 Walking Route:");
            summary.AppendLine($"• Distance: {route.TotalDistance:F1}m");
            summary.AppendLine($"• Estimated Time: {(route.TotalDistance / 80):F0} minutes"); // 80m/min walking speed
            summary.AppendLine("• No public transport routes found");
        }

        await DisplayAlert("Route Found", summary.ToString(), "OK");
    }

    private async void OnGrabberTapped(object? sender, TappedEventArgs e)
    {
        // Only allow interaction if there's a destination
        if (_hasDestination)
        {
            await ToggleBottomSheet();
        }
    }

    private async Task ToggleBottomSheet()
    {
        try
        {
            if (_isBottomSheetExpanded)
            {
                // Collapse: hide content, keep only grabber visible
                await SheetContent.FadeTo(0, 200);
                SheetContent.IsVisible = false;
                _isBottomSheetExpanded = false;
            }
            else
            {
                // Expand: show content
                SheetContent.IsVisible = true;
                await SheetContent.FadeTo(1, 200);
                _isBottomSheetExpanded = true;
            }
            
            // Update recenter button position after state change
            UpdateRecenterPosition();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error toggling bottom sheet: {ex.Message}");
        }
    }

    private async Task ExpandBottomSheet()
    {
        try
        {
            if (!_isBottomSheetExpanded)
            {
                // Expand: show content
                SheetContent.IsVisible = true;
                await SheetContent.FadeTo(1, 200);
                _isBottomSheetExpanded = true;
                
                // Update recenter button position after expansion
                UpdateRecenterPosition();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error expanding bottom sheet: {ex.Message}");
        }
    }

    private void UpdateBottomSheetInfo()
    {
        try
        {
            if (_currentRoute != null && _currentRoute.Segments.Any())
            {
                // Update route status
                RouteStatusLabel.Text = "Route 1";
                
                // Calculate fares
                var totalDistanceKm = _currentRoute.TotalDistance / 1000.0;
                var standardFare = CalculateStandardFare(totalDistanceKm);
                var discountedFare = standardFare * 0.8; // 20% discount
                
                StandardFareLabel.Text = $"Standard: ₱{standardFare:F2}";
                DiscountedFareLabel.Text = $"Discounted: ₱{discountedFare:F2}";
                
                // Update route distances
                RouteDistanceContainer.Children.Clear();
                
                for (int i = 0; i < _currentRoute.Segments.Count; i++)
                {
                    var segment = _currentRoute.Segments[i];
                    var distanceKm = segment.Distance / 1000.0;
                    
                    var routeLabel = new Label
                    {
                        Text = $"{segment.Route.Name}: {distanceKm:F1} km",
                        FontSize = 14,
                        TextColor = Color.FromArgb("#333"),
                        HorizontalOptions = LayoutOptions.Center
                    };
                    
                    RouteDistanceContainer.Children.Add(routeLabel);
                }
                
                // Add total distance
                var totalLabel = new Label
                {
                    Text = $"Total: {totalDistanceKm:F1} km",
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#333"),
                    HorizontalOptions = LayoutOptions.Center
                };
                RouteDistanceContainer.Children.Add(totalLabel);
            }
            else
            {
                // Default state - no route
                RouteStatusLabel.Text = "Default Route";
                
                // Default fares (13 pesos base)
                StandardFareLabel.Text = "Standard: ₱13.00";
                DiscountedFareLabel.Text = "Discounted: ₱10.40";
                
                // Default distance
                RouteDistanceContainer.Children.Clear();
                DefaultDistanceLabel.Text = "Default: 0.0 km";
                RouteDistanceContainer.Children.Add(DefaultDistanceLabel);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating bottom sheet info: {ex.Message}");
        }
    }

    private double CalculateStandardFare(double distanceKm)
    {
        // Standard fare: ₱13 base + ₱1.80 per succeeding kilometer
        const double baseFare = 13.0;
        const double perKmRate = 1.80;
        
        if (distanceKm <= 1.0)
        {
            return baseFare;
        }
        else
        {
            var succeedingKm = distanceKm - 1.0;
            return baseFare + (succeedingKm * perKmRate);
        }
    }

    private async Task InitializeBottomSheet()
    {
        try
        {
            // Start in collapsed state
            SheetContent.IsVisible = false;
            SheetContent.Opacity = 0;
            _isBottomSheetExpanded = false;
            
            // Update grabber appearance for non-interactive state
            UpdateGrabberAppearance();
            
            // Initialize with default content
            UpdateBottomSheetInfo();
            
            // Set initial recenter button position
            UpdateRecenterPosition();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing bottom sheet: {ex.Message}");
        }
    }

    private void UpdateGrabberAppearance()
    {
        try
        {
            // Change grabber color based on interactive state
            if (_hasDestination)
            {
                Grabber.BackgroundColor = Colors.Gray; // Interactive
            }
            else
            {
                Grabber.BackgroundColor = Colors.LightGray; // Non-interactive
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating grabber appearance: {ex.Message}");
        }
    }

    private void UpdateRecenterPosition(double gap = 15)
    {
        try
        {
            double offset = 0;

            if (BottomSheet?.IsVisible == true)
            {
                var h = BottomSheet.Height > 0 ? BottomSheet.Height : BottomSheet.HeightRequest;
                offset = Math.Max(0, h);
            }

            RecenterButton.Margin = new Thickness(0, 0, 12, offset + gap);
            
            System.Diagnostics.Debug.WriteLine($"Updated recenter button margin to: {offset + gap}px (bottom sheet height: {offset}px + gap: {gap}px)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating recenter button position: {ex.Message}");
        }
    }
}
