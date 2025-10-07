using JRoute.Models;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace JRoute.Services;

public class RouteVisualizationService
{
    private readonly Microsoft.Maui.Controls.Maps.Map _map;
    private readonly List<Polyline> _routePolylines = new();
    private readonly List<Pin> _routePins = new();

    public RouteVisualizationService(Microsoft.Maui.Controls.Maps.Map map)
    {
        _map = map;
    }

    public void ClearRoutes()
    {
        foreach (var polyline in _routePolylines)
        {
            _map.MapElements.Remove(polyline);
        }
        _routePolylines.Clear();

        foreach (var pin in _routePins)
        {
            _map.Pins.Remove(pin);
        }
        _routePins.Clear();
    }

    public void DisplayComputedRoute(ComputedRoute computedRoute, Location userLocation)
    {
        ClearRoutes();

        if (computedRoute.Segments.Any())
        {
            DisplaySegmentedRoute(computedRoute, userLocation);
        }
        else if (computedRoute.WalkingSegments.Any())
        {
            DisplayWalkingRoute(computedRoute.WalkingSegments);
        }
    }

    public void DisplayBaselineRoute(List<Location> baselineRoute)
    {
        if (!baselineRoute.Any()) return;

        var baselinePolyline = new Polyline
        {
            StrokeColor = Colors.LightBlue,
            StrokeWidth = 4
        };

        foreach (var point in baselineRoute)
        {
            baselinePolyline.Geopath.Add(point);
        }

        _map.MapElements.Add(baselinePolyline);
        _routePolylines.Add(baselinePolyline);
    }

    private void DisplaySegmentedRoute(ComputedRoute computedRoute, Location userLocation)
    {
        var colors = new[]
        {
            Colors.Blue,
            Colors.Green,
            Colors.Orange,
            Colors.Purple,
            Colors.Red
        };

        for (int i = 0; i < computedRoute.Segments.Count; i++)
        {
            var segment = computedRoute.Segments[i];
            var color = colors[i % colors.Length];

            // Draw route segment
            DrawRouteSegment(segment, color);

            // Add pickup pin
            AddRoutePin(segment.StartPoint, $"Pickup: {segment.Route.Name}", 
                       $"Board here for {segment.Route.Name}", PinType.Place, Colors.Green);

            // Add drop pin
            AddRoutePin(segment.EndPoint, $"Drop: {segment.Route.Name}", 
                       $"Get off here from {segment.Route.Name}", PinType.Place, Colors.Red);

            // Add walking segment to next pickup (if not last segment)
            if (i < computedRoute.Segments.Count - 1)
            {
                var nextSegment = computedRoute.Segments[i + 1];
                DrawWalkingPath(segment.EndPoint.Location, nextSegment.StartPoint.Location);
            }
        }

        // Add walking segment from user to first pickup
        if (computedRoute.Segments.Any())
        {
            var firstPickup = computedRoute.Segments.First().StartPoint.Location;
            DrawWalkingPath(userLocation, firstPickup);
        }
    }

    private void DrawRouteSegment(RouteSegment segment, Color color)
    {
        var polyline = new Polyline
        {
            StrokeColor = color,
            StrokeWidth = 6
        };

        // Add points from start index to end index
        for (int i = segment.StartIndex; i <= segment.EndIndex; i++)
        {
            if (i < segment.Route.Points.Count)
            {
                polyline.Geopath.Add(segment.Route.Points[i].Location);
            }
        }

        _map.MapElements.Add(polyline);
        _routePolylines.Add(polyline);
    }

    private void DrawWalkingPath(Location start, Location end)
    {
        var walkingPolyline = new Polyline
        {
            StrokeColor = Colors.Gray,
            StrokeWidth = 2 // Thinner line for walking
        };

        walkingPolyline.Geopath.Add(start);
        walkingPolyline.Geopath.Add(end);

        _map.MapElements.Add(walkingPolyline);
        _routePolylines.Add(walkingPolyline);
    }

    private void DisplayWalkingRoute(List<RoutePoint> walkingSegments)
    {
        if (walkingSegments.Count >= 2)
        {
            var walkingPolyline = new Polyline
            {
                StrokeColor = Colors.DarkGray,
                StrokeWidth = 3 // Slightly thicker for main walking route
            };

            foreach (var point in walkingSegments)
            {
                walkingPolyline.Geopath.Add(point.Location);
            }

            _map.MapElements.Add(walkingPolyline);
            _routePolylines.Add(walkingPolyline);

            // Add walking pins
            AddRoutePin(walkingSegments.First(), "Start Walking", "Begin your journey here", PinType.Place, Colors.Blue);
            AddRoutePin(walkingSegments.Last(), "Walking Destination", "Your destination", PinType.Place, Colors.Red);
        }
    }

    private void AddRoutePin(RoutePoint point, string label, string address, PinType type, Color color)
    {
        var pin = new Pin
        {
            Label = label,
            Address = address,
            Type = type,
            Location = point.Location
        };

        _map.Pins.Add(pin);
        _routePins.Add(pin);
    }

    public void FocusOnRoute(ComputedRoute computedRoute, Location userLocation)
    {
        var allLocations = new List<Location> { userLocation };

        foreach (var segment in computedRoute.Segments)
        {
            allLocations.Add(segment.StartPoint.Location);
            allLocations.Add(segment.EndPoint.Location);
        }

        if (computedRoute.WalkingSegments.Any())
        {
            allLocations.AddRange(computedRoute.WalkingSegments.Select(p => p.Location));
        }

        if (allLocations.Count > 1)
        {
            var bounds = CalculateBounds(allLocations);
            _map.MoveToRegion(bounds);
        }
    }

    private MapSpan CalculateBounds(List<Location> locations)
    {
        var minLat = locations.Min(l => l.Latitude);
        var maxLat = locations.Max(l => l.Latitude);
        var minLon = locations.Min(l => l.Longitude);
        var maxLon = locations.Max(l => l.Longitude);

        var centerLat = (minLat + maxLat) / 2;
        var centerLon = (minLon + maxLon) / 2;

        var latDelta = Math.Max(maxLat - minLat, 0.01) * 1.2; // Add 20% padding
        var lonDelta = Math.Max(maxLon - minLon, 0.01) * 1.2;

        return new MapSpan(
            new Location(centerLat, centerLon),
            latDelta,
            lonDelta
        );
    }
}
