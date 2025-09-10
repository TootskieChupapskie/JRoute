using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Maui.Maps;

// Aliases to avoid 'Map' ambiguities
using MauiMap      = Microsoft.Maui.Controls.Maps.Map;
using MauiPolyline = Microsoft.Maui.Controls.Maps.Polyline;
using MauiLocation = Microsoft.Maui.Devices.Sensors.Location;
using MauiColor    = Microsoft.Maui.Graphics.Color;

public static class SimpleGeoJsonLineRenderer
{
    private static readonly MauiColor Orange = MauiColor.FromArgb("#FF8A00"); // always orange

    public static void Render(MauiMap map, string geojson, float strokeWidth = 12f)
    {
        map.MapElements.Clear();

        using var doc = JsonDocument.Parse(geojson);
        var root = doc.RootElement;

        var allPts = new List<MauiLocation>();
        foreach (var segment in EnumerateLineSegments(root))
        {
            var pl = new MauiPolyline { StrokeColor = Orange, StrokeWidth = strokeWidth };
            foreach (var p in segment)
            {
                pl.Geopath.Add(p);
                allPts.Add(p);
            }
            map.MapElements.Add(pl);
        }

        if (allPts.Count > 0) FitTo(map, allPts);
    }

    // ---- GeoJSON parsing (lines only) ----
    private static IEnumerable<IEnumerable<MauiLocation>> EnumerateLineSegments(JsonElement elem)
    {
        var type = elem.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(type)) yield break;

        switch (type)
        {
            case "FeatureCollection":
                foreach (var f in elem.GetProperty("features").EnumerateArray())
                    foreach (var seg in EnumerateLineSegments(f))
                        yield return seg;
                yield break;

            case "Feature":
                foreach (var seg in EnumerateLineSegments(elem.GetProperty("geometry")))
                    yield return seg;
                yield break;

            case "GeometryCollection":
                foreach (var g in elem.GetProperty("geometries").EnumerateArray())
                    foreach (var seg in EnumerateLineSegments(g))
                        yield return seg;
                yield break;

            case "LineString":
                yield return ReadLine(elem.GetProperty("coordinates"));
                yield break;

            case "MultiLineString":
                foreach (var line in elem.GetProperty("coordinates").EnumerateArray())
                    yield return ReadLine(line);
                yield break;

            // Treat polygon outer rings like lines (no holes)
            case "Polygon":
                {
                    var rings = elem.GetProperty("coordinates");
                    if (rings.GetArrayLength() > 0)
                        yield return ReadLine(rings[0]);
                    yield break;
                }

            case "MultiPolygon":
                foreach (var poly in elem.GetProperty("coordinates").EnumerateArray())
                    if (poly.GetArrayLength() > 0)
                        yield return ReadLine(poly[0]);
                yield break;

            // Points are ignored for this "big line" renderer
            default:
                yield break;
        }
    }

    private static IEnumerable<MauiLocation> ReadLine(JsonElement coords)
    {
        foreach (var c in coords.EnumerateArray())
        {
            var lon = c[0].GetDouble(); var lat = c[1].GetDouble();
            yield return new MauiLocation(lat, lon);
        }
    }

    private static void FitTo(MauiMap map, List<MauiLocation> pts)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

        foreach (var p in pts)
        {
            if (p.Latitude  < minLat) minLat = p.Latitude;
            if (p.Latitude  > maxLat) maxLat = p.Latitude;
            if (p.Longitude < minLon) minLon = p.Longitude;
            if (p.Longitude > maxLon) maxLon = p.Longitude;
        }

        var center = new MauiLocation((minLat + maxLat) / 2.0, (minLon + maxLon) / 2.0);

        var dLat = Distance.BetweenPositions(new MauiLocation(minLat, center.Longitude),
                                             new MauiLocation(maxLat, center.Longitude)).Kilometers;
        var dLon = Distance.BetweenPositions(new MauiLocation(center.Latitude, minLon),
                                             new MauiLocation(center.Latitude, maxLon)).Kilometers;
        var radiusKm = Math.Max(dLat, dLon) / 2.0;
        if (radiusKm < 0.5) radiusKm = 0.5;

        map.MoveToRegion(MapSpan.FromCenterAndRadius(center, Distance.FromKilometers(radiusKm)));
    }
}
