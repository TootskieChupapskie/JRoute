using Microsoft.Maui.Maps;

namespace JRoute.Models;

public class RoutePoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Name { get; set; }
    public Location Location => new Location(Latitude, Longitude);
}

public class JeepneyRoute
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<RoutePoint> Points { get; set; } = new();
    public double Fare { get; set; }
    public string VehicleType { get; set; } = "Jeepney";
    public TimeSpan EstimatedDuration { get; set; }
    public List<string> Landmarks { get; set; } = new();
}

public class RouteSegment
{
    public JeepneyRoute Route { get; set; } = new();
    public RoutePoint StartPoint { get; set; } = new();
    public RoutePoint EndPoint { get; set; } = new();
    public int StartIndex { get; set; }
    public int EndIndex { get; set; }
    public double Distance { get; set; }
    public TimeSpan Duration { get; set; }
    public double Fare { get; set; }
}

public class ComputedRoute
{
    public List<RouteSegment> Segments { get; set; } = new();
    public double TotalDistance { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public double TotalFare { get; set; }
    public int TransferCount => Segments.Count - 1;
    public List<RoutePoint> WalkingSegments { get; set; } = new();
}

public class RouteMatch
{
    public JeepneyRoute Route { get; set; } = new();
    public RoutePoint NearestPickupPoint { get; set; } = new();
    public RoutePoint NearestDropPoint { get; set; } = new();
    public double PickupDistance { get; set; }
    public double DropDistance { get; set; }
    public double RouteOverlap { get; set; }
    public int PickupIndex { get; set; }
    public int DropIndex { get; set; }
}
