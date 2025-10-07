using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Supabase;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace JRoute.Pages.Conductor;

public partial class Conductor : ContentPage
{
    private const string StorageBucket = "Jroute";
    private const string StoragePrefix = "routes/";
    private string? _lastRenderedPath;
    private List<Location> _routePath = new();
    private readonly List<Microsoft.Maui.Controls.Maps.Polyline> _guideDashes = new();
    private const double OnRouteThresholdMeters = 15.0;
    private int _passengerCount = 0;
    private int _capacity = 0;
    private bool _isCollapsed;
    private double _expandedHeight;
    private double _collapsedHeight;
    private bool _sanitizingSeats;
    private Task CheckAndGuideToRouteAsync() => Task.CompletedTask;
    private readonly Label _measure = new() { IsVisible = false };
    private static readonly Color RouteTextVisible = Colors.White;
    private static readonly Color RouteTextGhost   = Color.FromArgb("#01FFFFFF");
    private IGeoJsonProvider? _geo;
    private string _currentSuggestion = string.Empty; // Track current suggestion
    
    private IGeoJsonProvider GeoProvider =>
        _geo ??= (Application.Current?.Handler?.MauiContext?.Services
                    .GetService<IGeoJsonProvider>()
                ?? throw new InvalidOperationException("IGeoJsonProvider not registered."));

    private readonly List<string> _routeDictionary = new()
    {
        "Bago Aplaya", "Bangkal", "Barrio Obrero", "Buhangin Via Dacudao", "Buhangin Via JP. Laurel", "Bunawan Via Buhangin", "Bunawan Via Sasa",
        "Calinan", "Camp Catitipan Via JP. Laurel", "Catalunan Grande", "Ecoland", "El Rio", "Toril"
    };

    private void UpdatePassengerUI()
    {
        if (_capacity < 0) _capacity = 0;
        if (_passengerCount < 0) _passengerCount = 0;
        if (_passengerCount > _capacity) _passengerCount = _capacity;

        PassengerCountLabel.Text = $"{_passengerCount}/{_capacity}";
    }

    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"[^\p{L}\p{Nd}\s_-]", "");
        t = Regex.Replace(t, @"[\s_]+", "-");
        t = Regex.Replace(t, "-{2,}", "-").Trim('-');
        return t;
    }

    private void SwapToPassengerBar()
    {
        _capacity = Math.Max(1, int.TryParse(SeatsEntry?.Text, out var cap) ? cap : 1);
        _passengerCount = Math.Min(Math.Max(_passengerCount, 0), _capacity);
        UpdatePassengerUI();

        BottomSheet.IsVisible = false;
        PassengerBar.IsVisible = true;
    }

    private async void OnBiyaheClicked(object sender, EventArgs e)
    {
        var route = (RouteEntry.Text ?? string.Empty).Trim();
        var capacityText = (SeatsEntry.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(capacityText))
        {
            await DisplayAlert(null ,"Fill in all Fields", "OK");
            return;
        }

        var routeUpper = route.ToUpperInvariant();
        
        var routeExists = _routeDictionary.Any(r => 
            r.Equals(routeUpper, StringComparison.OrdinalIgnoreCase));
        
        if (!routeExists)
        {
            await DisplayAlert("Route Not Available", 
                $"The route '{routeUpper}' is not available. Please select a valid route.", "OK");
            return;
        }

        var loaded = await LoadAndRenderRouteAsync(routeUpper);
        if (!loaded)
        {
            await DisplayAlert("Route Load Failed", 
                $"The route '{routeUpper}' could not be loaded. Please try again or contact support.", "OK");
            return;
        }

        SwapToPassengerBar();
        Dispatcher.Dispatch(() => UpdateRecenterPosition());
        await RecenterToUserAsync();
    }

    public Conductor()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;

        RouteEntry.SizeChanged += (_, __) =>
            ShowInlineSuggestion(RouteEntry.Text ?? string.Empty);

        BottomSheet.SizeChanged += (_, __) =>
        {
            if (_expandedHeight <= 0 && BottomSheet.Height > 0)
            {
                _expandedHeight = BottomSheet.Height;
                _collapsedHeight = ComputeCollapsedHeight();
            }
            UpdateRecenterPosition();
        };
        BottomSheet.SizeChanged += BottomSheet_FirstMeasuredOnce;

        PassengerBar.SizeChanged += (_, __) => UpdateRecenterPosition();
    }

    private void UpdateRecenterPosition(double gap = 12)
    {
        double offset = 0;

        if (BottomSheet?.IsVisible == true)
        {
            var h = BottomSheet.Height > 0 ? BottomSheet.Height : BottomSheet.HeightRequest;
            offset = Math.Max(0, h);
        }
        else if (PassengerBar?.IsVisible == true)
        {
            var h = PassengerBar.Height > 0 ? PassengerBar.Height : PassengerBar.HeightRequest;
            offset = Math.Max(0, h);
        }

        RecenterButton.Margin = new Thickness(0, 0, 12, offset + gap);
    }

    private void BottomSheet_FirstMeasuredOnce(object? sender, EventArgs e)
    {
        if (BottomSheet.Height <= 0) return;
        UpdateRecenterPosition();
        BottomSheet.SizeChanged -= BottomSheet_FirstMeasuredOnce;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Dispatcher.Dispatch(() => UpdateRecenterPosition());
    }

    private double MeasureTextWidth(string text)
    {
        _measure.Text = text ?? string.Empty;
        _measure.FontFamily = RouteEntry.FontFamily;
        _measure.FontSize = RouteEntry.FontSize;
        _measure.FontAttributes = RouteEntry.FontAttributes;
        _measure.CharacterSpacing = RouteEntry.CharacterSpacing;

        var size = _measure.Measure(double.PositiveInfinity, double.PositiveInfinity);
        return size.Width;
    }

    private void EnterRouteEditMode()
    {
        // Make Entry text nearly invisible so overlay shows through
        RouteEntry.TextColor = Color.FromArgb("#01FFFFFF");
        var typedUpper = (RouteEntry.Text ?? string.Empty).ToUpperInvariant();
        ShowInlineSuggestion(typedUpper);
    }

    private void LeaveRouteEditMode()
    {
        if (!string.IsNullOrEmpty(RouteEntry.Text))
            RouteEntry.Text = RouteEntry.Text.ToUpperInvariant();

        RouteEntry.TextColor = RouteTextVisible;
        RouteOverlay.IsVisible = false;
        RouteOverlay.FormattedText = null;
        _currentSuggestion = string.Empty;
    }
    
    private void ShowInlineSuggestion(string typed)
    {
        try
        {
            RouteOverlay.IsVisible = false;
            RouteOverlay.FormattedText = null;
            _currentSuggestion = string.Empty;

            if (string.IsNullOrEmpty(typed))
            {
                // No text typed, make Entry visible again
                RouteEntry.TextColor = Colors.White;
                return;
            }

            var typedUpper = typed.ToUpperInvariant();
            var match = _routeDictionary.FirstOrDefault(r =>
                r.StartsWith(typedUpper, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(match) || typedUpper.Length >= match.Length)
            {
                // No suggestion or fully typed, show Entry text normally
                RouteEntry.TextColor = Colors.White;
                RouteOverlay.IsVisible = false;
                return;
            }

            _currentSuggestion = match.ToUpperInvariant();

            // Make Entry text invisible so overlay shows
            RouteEntry.TextColor = Color.FromArgb("#01FFFFFF");

            // Show typed part + suggestion together (BOLD)
            var remaining = match.Substring(typedUpper.Length).ToUpperInvariant();
            var fs = new FormattedString();
            
            // Show the full text: typed part (white, bold) + remaining (ghost, bold)
            fs.Spans.Add(new Span 
            { 
                Text = typedUpper, 
                TextColor = Colors.White,
                FontAttributes = FontAttributes.Bold 
            });
            fs.Spans.Add(new Span 
            { 
                Text = remaining, 
                TextColor = Color.FromArgb("#66FFFFFF"),
                FontAttributes = FontAttributes.Bold 
            });

            RouteOverlay.FormattedText = fs;
            RouteOverlay.IsVisible = true;
        }
        catch
        {
            // Silently fail to prevent freezing
            RouteOverlay.IsVisible = false;
            RouteEntry.TextColor = Colors.White;
        }
    }

    private bool _isUpdatingRouteText = false;

    private void OnRouteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender != RouteEntry || _isUpdatingRouteText) return;

        var newText = (e.NewTextValue ?? string.Empty).ToUpperInvariant();
        if (newText != e.NewTextValue)
        {
            _isUpdatingRouteText = true;
            RouteEntry.Text = newText;
            _isUpdatingRouteText = false;
            return;
        }

        // Refresh suggestion on every keystroke
        ShowInlineSuggestion(newText);
    }

    private async void OnGrabberTapped(object? sender, TappedEventArgs e)
    {
        if (_expandedHeight <= 0)
        {
            var measured = BottomSheet.Height > 0 ? BottomSheet.Height : BottomSheet.HeightRequest;
            _expandedHeight = Math.Max(measured, 240d);
            _collapsedHeight = ComputeCollapsedHeight();
        }

        if (!_isCollapsed)
        {
            _isCollapsed = true;
            SheetContent.IsVisible = false;
            await AnimateHeight(BottomSheet.Height, _collapsedHeight);
        }
        else
        {
            _isCollapsed = false;
            await AnimateHeight(BottomSheet.Height, _expandedHeight);
            SheetContent.IsVisible = true;
        }

        UpdateRecenterPosition();
        RouteOverlay.IsVisible = false;
    }

    private double ComputeCollapsedHeight()
    {
        var pad = BottomSheet.Padding;
        var grabberH = Grabber.Height > 0 ? Grabber.Height : Grabber.HeightRequest;
        if (grabberH <= 0) grabberH = 8;
        var collapsed = pad.Top + grabberH + pad.Bottom + 16;
        return Math.Max(collapsed, 56);
    }

    private Task AnimateHeight(double from, double to)
    {
        var tcs = new TaskCompletionSource();
        var anim = new Animation(v => BottomSheet.HeightRequest = v, from, to, Easing.CubicInOut);
        anim.Commit(BottomSheet, "SheetHeightAnim", 16, 220, finished: (_, __) => tcs.SetResult());
        return tcs.Task;
    }

    private void OnEntryFocused(object sender, FocusEventArgs e)
    {
        if (sender is Entry entry && string.IsNullOrEmpty(entry.Text))
            entry.Placeholder = string.Empty;

        if (sender == RouteEntry)
            EnterRouteEditMode();
    }

    private void OnEntryUnfocused(object sender, FocusEventArgs e)
    {
        if (sender is Entry entry && string.IsNullOrEmpty(entry.Text))
            entry.Placeholder = entry == RouteEntry ? "CHOOSE ROUTE" : "MAX NO. OF SEATS";

        if (sender == RouteEntry)
            LeaveRouteEditMode();
    }

    private async void OnRouteCompleted(object sender, EventArgs e)
    {
        if (sender is not Entry entry) return;

        // Auto-fill with current suggestion when Enter is pressed
        if (!string.IsNullOrEmpty(_currentSuggestion))
        {
            entry.Text = _currentSuggestion;
            RouteOverlay.IsVisible = false;
            _currentSuggestion = string.Empty;
            
            // Optional: Load the route immediately
            await LoadAndRenderRouteAsync(entry.Text);
        }
    }

    private async Task<bool> LoadAndRenderRouteAsync(string routeNameUpper)
    {
        try
        {
            var slug = Slugify(routeNameUpper);
            var path = $"{StoragePrefix}{slug}.geojson";

            if (_lastRenderedPath == path) return true;

            var geojson = await GeoProvider.GetAsync(StorageBucket, path);
            SimpleGeoJsonLineRenderer.Render(MyMap, geojson);
            _lastRenderedPath = path;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load route: {ex.Message}");
            return false;
        }
    }

    private void OnSeatsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sanitizingSeats || sender is not Entry entry) return;

        var digits = new string((e.NewTextValue ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits != e.NewTextValue) { _sanitizingSeats = true; entry.Text = digits; _sanitizingSeats = false; }

        _capacity = Math.Max(0, int.TryParse(digits, out var cap) ? cap : 0);
        if (_passengerCount > _capacity) _passengerCount = _capacity;
        UpdatePassengerUI();
    }

    private async void OnDecrementTapped(object sender, TappedEventArgs e)
    {
        await BounceAsync(DecrementTile);

        if (_passengerCount > 0)
        {
            _passengerCount--;
            UpdatePassengerUI();
            await PulseCountAsync();
        }
    }

    private async void OnIncrementTapped(object sender, TappedEventArgs e)
    {
        await BounceAsync(IncrementTile);

        if (_passengerCount < _capacity)
        {
            _passengerCount++;
            UpdatePassengerUI();
            await PulseCountAsync();
        }
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

    private async Task PulseCountAsync()
    {
        try
        {
            await PassengerCountLabel.ScaleTo(1.08, 80, Easing.CubicOut);
            await PassengerCountLabel.ScaleTo(1.0, 120, Easing.CubicIn);
        }
        catch { }
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

                await CheckAndGuideToRouteAsync();
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