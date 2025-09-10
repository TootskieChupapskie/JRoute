using System.Collections.Generic;
using System.Text.Json;                       // <-- add this
using Microsoft.Maui.Maps;                    // Distance, MapSpan
using Microsoft.Maui.Graphics;

using MauiMap      = Microsoft.Maui.Controls.Maps.Map;
using MauiPolyline = Microsoft.Maui.Controls.Maps.Polyline;
using MauiPolygon  = Microsoft.Maui.Controls.Maps.Polygon;
using MauiPin      = Microsoft.Maui.Controls.Maps.Pin;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;
using MauiColor    = Microsoft.Maui.Graphics.Color;

public static class GeoJsonMapRenderer
{
    public static void Render(MauiMap map, string geojson) // <-- MauiMap
    {
        map.MapElements.Clear();
        using var doc = JsonDocument.Parse(geojson);
        var root = doc.RootElement;

        var all = new List<MauiLocation>();               // <-- MauiLocation
        switch (root.GetProperty("type").GetString())
        {
            case "FeatureCollection":
                foreach (var f in root.GetProperty("features").EnumerateArray())
                    RenderFeature(map, f, all);
                break;
            case "Feature":
                RenderFeature(map, root, all);
                break;
            default:
                RenderGeometry(map, root, default, all);
                break;
        }

        if (all.Count > 0) FitTo(map, all);
    }

    static void RenderFeature(MauiMap map, JsonElement feature, List<MauiLocation> all)
    {
        var geom = feature.GetProperty("geometry");
        feature.TryGetProperty("properties", out var props);
        RenderGeometry(map, geom, props, all);
    }

    static void RenderGeometry(MauiMap map, JsonElement geom, JsonElement props, List<MauiLocation> all)
    {
        var type = geom.GetProperty("type").GetString();

        var stroke        = TryColor(props, "stroke")        ?? Colors.Red;
        var strokeOpacity = TryDouble(props, "stroke-opacity") ?? 1.0;
        var strokeWidth   = (float)(TryDouble(props, "stroke-width") ?? 4.0);
        var fill          = TryColor(props, "fill")          ?? Colors.Transparent;
        var fillOpacity   = TryDouble(props, "fill-opacity")   ?? 0.3;

        stroke = stroke.WithAlpha((float)strokeOpacity);
        fill   = fill.WithAlpha((float)fillOpacity);
        
        switch (type)
        {
            case "Point":
            {
                var p = ReadPosition(geom.GetProperty("coordinates"));
                map.Pins.Add(new MauiPin { Label = TryString(props, "name") ?? "Point", Location = p });
                all.Add(p);
                break;
            }
            case "MultiPoint":
            {
                foreach (var c in geom.GetProperty("coordinates").EnumerateArray())
                {
                    var p = ReadPosition(c);
                    map.Pins.Add(new MauiPin { Label = TryString(props, "name") ?? "Point", Location = p });
                    all.Add(p);
                }
                break;
            }
            case "LineString":
            {
                var pl = new MauiPolyline { StrokeColor = stroke, StrokeWidth = strokeWidth };
                foreach (var c in geom.GetProperty("coordinates").EnumerateArray())
                {
                    var p = ReadPosition(c);
                    pl.Geopath.Add(p);
                    all.Add(p);
                }
                map.MapElements.Add(pl);
                break;
            }
            case "MultiLineString":
            {
                foreach (var line in geom.GetProperty("coordinates").EnumerateArray())
                {
                    var pl = new MauiPolyline { StrokeColor = stroke, StrokeWidth = strokeWidth };
                    foreach (var c in line.EnumerateArray())
                    {
                        var p = ReadPosition(c);
                        pl.Geopath.Add(p);
                        all.Add(p);
                    }
                    map.MapElements.Add(pl);
                }
                break;
            }
            case "Polygon":
            {
                var rings = geom.GetProperty("coordinates");
                if (rings.GetArrayLength() > 0)
                {
                    var pg = new MauiPolygon { StrokeColor = stroke, StrokeWidth = strokeWidth, FillColor = fill };
                    foreach (var c in rings[0].EnumerateArray()) // outer ring
                    {
                        var p = ReadPosition(c);
                        pg.Geopath.Add(p);
                        all.Add(p);
                    }
                    map.MapElements.Add(pg);
                }
                break;
            }
            case "MultiPolygon":
            {
                foreach (var poly in geom.GetProperty("coordinates").EnumerateArray())
                {
                    if (poly.GetArrayLength() > 0)
                    {
                        var pg = new MauiPolygon { StrokeColor = stroke, StrokeWidth = strokeWidth, FillColor = fill };
                        foreach (var c in poly[0].EnumerateArray()) // outer ring
                        {
                            var p = ReadPosition(c);
                            pg.Geopath.Add(p);
                            all.Add(p);
                        }
                        map.MapElements.Add(pg);
                    }
                }
                break;
            }
            case "GeometryCollection":
            {
                foreach (var g in geom.GetProperty("geometries").EnumerateArray())
                    RenderGeometry(map, g, props, all);
                break;
            }
        }
    }

    static MauiLocation ReadPosition(JsonElement coord)
    {
        var lon = coord[0].GetDouble();
        var lat = coord[1].GetDouble();
        return new MauiLocation(lat, lon);
    }

    static void FitTo(MauiMap map, List<MauiLocation> pts)
    {
        double minLat = pts.Min(p => p.Latitude);
        double maxLat = pts.Max(p => p.Latitude);
        double minLon = pts.Min(p => p.Longitude);
        double maxLon = pts.Max(p => p.Longitude);

        var center = new MauiLocation((minLat + maxLat) / 2.0, (minLon + maxLon) / 2.0);

        var dLat = Distance.BetweenPositions(new MauiLocation(minLat, center.Longitude),
                                             new MauiLocation(maxLat, center.Longitude)).Kilometers;
        var dLon = Distance.BetweenPositions(new MauiLocation(center.Latitude, minLon),
                                             new MauiLocation(center.Latitude, maxLon)).Kilometers;

        var radiusKm = Math.Max(dLat, dLon) / 2.0;
        if (radiusKm < 0.5) radiusKm = 0.5;

        map.MoveToRegion(MapSpan.FromCenterAndRadius(center, Distance.FromKilometers(radiusKm)));
    }

    static string? TryString(JsonElement props, string name)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? TryDouble(JsonElement props, string name)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetDouble() : (double?)null;

    static MauiColor? TryColor(JsonElement props, string name)
    {
        var hex = TryString(props, name);
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return MauiColor.FromArgb(hex.StartsWith("#") ? hex : "#" + hex); }
        catch { return null; }
    }
}
