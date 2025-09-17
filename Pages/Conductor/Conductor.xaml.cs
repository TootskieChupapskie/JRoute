using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Supabase; // for Client
using Microsoft.Extensions.DependencyInjection; // to resolve services
using System.Text.RegularExpressions;

namespace JRoute.Pages.Conductor;

public partial class Conductor : ContentPage
{
    private const string StorageBucket = "Jroute";   // <-- change if your bucket is different
    private const string StoragePrefix = "routes/";  // folder path inside the bucket
    private string? _lastRenderedPath;
    private List<Location> _routePath = new();
    private readonly List<Microsoft.Maui.Controls.Maps.Polyline> _guideDashes = new();
    private const double OnRouteThresholdMeters = 15.0;
    private int _passengerCount = 0;   // was 1
    private int _capacity = 0;         // will be set from SeatsEntry
    private bool _isCollapsed;
    private double _expandedHeight;
    private double _collapsedHeight;
    private bool _sanitizingSeats;
    private Task CheckAndGuideToRouteAsync() => Task.CompletedTask;
    private readonly Label _measure = new() { IsVisible = false };
    private static readonly Color RouteTextVisible = Colors.White;                 // show when NOT editing
    private static readonly Color RouteTextGhost   = Color.FromArgb("#01FFFFFF");  // almost transparent while editing
    private IGeoJsonProvider? _geo;
    private IGeoJsonProvider GeoProvider =>
        _geo ??= (Application.Current?.Handler?.MauiContext?.Services
                    .GetService<IGeoJsonProvider>()
                ?? throw new InvalidOperationException("IGeoJsonProvider not registered."));


    private readonly List<string> _routeDictionary = new()
    {
        "Bago Aplaya", "Bankal", "Barrio Obrero", "Buhangin Via Dacudao", "Buhangin Via JP. Laurel", "Bunawan Via Buhangin", "Bunawan Via Sasa",
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
        t = Regex.Replace(t, @"[^\p{L}\p{Nd}\s_-]", ""); // keep letters/digits/space/_/-
        t = Regex.Replace(t, @"[\s_]+", "-");
        t = Regex.Replace(t, "-{2,}", "-").Trim('-');
        return t;
    }

    private void SwapToPassengerBar()
    {
        _capacity = Math.Max(1, int.TryParse(SeatsEntry?.Text, out var cap) ? cap : 1);
        _passengerCount = Math.Min(Math.Max(_passengerCount, 0), _capacity);
        UpdatePassengerUI();

        BottomSheet.IsVisible = false;   // hide the modal sheet
        PassengerBar.IsVisible = true;   // show the 3-square bar
    }

    // In your click handler:
    private async void OnBiyaheClicked(object sender, EventArgs e)
    {
        var route = (RouteEntry.Text ?? string.Empty).Trim();
        var capacityText = (SeatsEntry.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(capacityText))
        {
            await DisplayAlert(null ,"Fill in all Fields", "OK");
            return;
        }

        await LoadAndRenderRouteAsync(route.ToUpperInvariant());
        SwapToPassengerBar();
        Dispatcher.Dispatch(() => UpdateRecenterPosition());
        await RecenterToUserAsync(); // same behavior as recenter tap
    }



    public Conductor()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;

        // keep inline suggestion refresh
        RouteEntry.SizeChanged += (_, __) =>
            ShowInlineSuggestion(RouteEntry.Text ?? string.Empty);

        // bottom sheet sizing (existing)
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

        // NEW: keep recenter button above PassengerBar too
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

        // In MAUI this returns Size (not SizeRequest)
        var size = _measure.Measure(double.PositiveInfinity, double.PositiveInfinity);
        return size.Width;
    }

    private void EnterRouteEditMode()
    {
        RouteEntry.TextColor = RouteTextGhost; // keep caret visible, hide Entry text
        var typedUpper = (RouteEntry.Text ?? string.Empty).ToUpperInvariant();
        UpdateRouteOverlay(typedUpper);        // overlay renders visible text
    }

    private void LeaveRouteEditMode()
    {
        // Make the Entry text itself visible when not focused
        if (!string.IsNullOrEmpty(RouteEntry.Text))
            RouteEntry.Text = RouteEntry.Text.ToUpperInvariant(); // keep style consistent

        RouteEntry.TextColor = RouteTextVisible;
        RouteOverlay.IsVisible = false;
        RouteOverlay.FormattedText = null;
    }
    
    private void ShowInlineSuggestion(string typed)
    {
        var match = _routeDictionary.FirstOrDefault(r =>
            r.StartsWith(typed ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        RouteOverlay.IsVisible = false;
        if (string.IsNullOrEmpty(typed) && string.IsNullOrEmpty(match)) { RouteOverlay.FormattedText = null; return; }

        var fs = new FormattedString();
        if (!string.IsNullOrEmpty(typed)) fs.Spans.Add(new Span { Text = typed, TextColor = Colors.White });
        if (!string.IsNullOrEmpty(match) && typed.Length < match.Length)
            fs.Spans.Add(new Span { Text = match.Substring(typed.Length), TextColor = Color.FromArgb("#66FFFFFF") });

        RouteOverlay.FormattedText = fs.Spans.Count > 0 ? fs : null;
        RouteOverlay.IsVisible = fs.Spans.Count > 0;
    }
    
    private void UpdateRouteOverlay(string typedUpper)
    {
        // Hide if empty — no auto "EL RIO" on focus
        if (string.IsNullOrEmpty(typedUpper))
        {
            RouteOverlay.IsVisible = false;
            RouteOverlay.FormattedText = null;
            return;
        }

        var fs = new FormattedString();
        // Typed part: bold, solid white
        fs.Spans.Add(new Span { Text = typedUpper, TextColor = Colors.White, FontAttributes = FontAttributes.Bold });

        // Suggest ONLY after first letter
        if (typedUpper.Length == 1)
        {
            var match = _routeDictionary.FirstOrDefault(r =>
                r.StartsWith(typedUpper, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(match) && match.Length > 1)
            {
                var remaining = match.Substring(1).ToUpperInvariant(); // ALL CAPS
                fs.Spans.Add(new Span
                {
                    Text = remaining,
                    TextColor = Color.FromArgb("#B3FFFFFF"), // 70% white
                    FontAttributes = FontAttributes.Bold
                });
            }
        }

        RouteOverlay.FormattedText = fs;
        RouteOverlay.IsVisible = true;
    }

    private void OnRouteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender != RouteEntry) return;

        // never mutate entry.Text here; just update the overlay
        var typedUpper = (e.NewTextValue ?? string.Empty).ToUpperInvariant();
        UpdateRouteOverlay(typedUpper);
    }

    private async void OnGrabberTapped(object? sender, TappedEventArgs e)
    {
        // Never hide the sheet in this handler — only resize it.
        if (_expandedHeight <= 0)
        {
            var measured = BottomSheet.Height > 0 ? BottomSheet.Height : BottomSheet.HeightRequest;
            _expandedHeight = Math.Max(measured, 240d);
            _collapsedHeight = ComputeCollapsedHeight();
        }

        if (!_isCollapsed)
        {
            _isCollapsed = true;
            SheetContent.IsVisible = false; // keep grabber visible, hide body
            await AnimateHeight(BottomSheet.Height, _collapsedHeight);
        }
        else
        {
            _isCollapsed = false;
            await AnimateHeight(BottomSheet.Height, _expandedHeight);
            SheetContent.IsVisible = true;
        }

        UpdateRecenterPosition();     // float button above current height
        RouteOverlay.IsVisible = false;
    }


    private double ComputeCollapsedHeight()
    {
        var pad = BottomSheet.Padding;
        var grabberH = Grabber.Height > 0 ? Grabber.Height : Grabber.HeightRequest;
        if (grabberH <= 0) grabberH = 8; // fallback matches your XAML
        // leave a tappable strip for the grabber
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

   // Update your handlers
    private void OnEntryFocused(object sender, FocusEventArgs e)
    {
        if (sender is Entry entry && string.IsNullOrEmpty(entry.Text))
            entry.Placeholder = string.Empty;

        if (sender == RouteEntry)
            EnterRouteEditMode();   // only for RouteEntry
    }

    private void OnEntryUnfocused(object sender, FocusEventArgs e)
    {
        if (sender is Entry entry && string.IsNullOrEmpty(entry.Text))
            entry.Placeholder = entry == RouteEntry ? "CHOOSE ROUTE" : "MAX NO. OF SEATS";

        if (sender == RouteEntry)
            LeaveRouteEditMode();   // show real text when you leave the field
    }

    private async void OnRouteCompleted(object sender, EventArgs e)
    {
        if (sender is not Entry entry) return;

        var typedUpper = (entry.Text ?? string.Empty).Trim().ToUpperInvariant();

        // If they pressed Enter after 1st letter, commit suggestion first
        if (typedUpper.Length == 1)
        {
            var match = _routeDictionary.FirstOrDefault(r =>
                r.StartsWith(typedUpper, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match))
            {
                entry.Text = match.ToUpperInvariant();
                UpdateRouteOverlay(entry.Text);
                typedUpper = entry.Text;
            }
        }

        if (!string.IsNullOrWhiteSpace(typedUpper))
            await LoadAndRenderRouteAsync(typedUpper);

        // ❌ Do NOT swap UI here
    }


    private async Task LoadAndRenderRouteAsync(string routeNameUpper)
    {
        try
        {
            var slug = Slugify(routeNameUpper);
            var path = $"{StoragePrefix}{slug}.geojson";

            if (_lastRenderedPath == path) return;

            var geojson = await GeoProvider.GetAsync(StorageBucket, path);
            SimpleGeoJsonLineRenderer.Render(MyMap, geojson);  // orange line
            _lastRenderedPath = path;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Route", $"Couldn't load route:\n{ex.Message}", "OK");
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

    //Animation helpers
    private async Task BounceAsync(VisualElement v)
    {
        if (v == null) return;
        try
        {
            await v.ScaleTo(0.92, 80, Easing.CubicOut);
            await v.ScaleTo(1.0, 120, Easing.CubicIn);
        }
        catch { /* ignore if view disposed */ }
    }

    private async Task PulseCountAsync()
    {
        try
        {
            await PassengerCountLabel.ScaleTo(1.08, 80, Easing.CubicOut);
            await PassengerCountLabel.ScaleTo(1.0, 120, Easing.CubicIn);
        }
        catch { /* ignore if view disposed */ }
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

                // If you added the dotted-guide logic, keep it in sync:
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
        await BounceAsync(RecenterButton);   // keep the tap animation
        await RecenterToUserAsync();         // shared logic
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
