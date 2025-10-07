using Newtonsoft.Json;
using System.Text;
using Microsoft.Maui.Maps;

namespace JRoute.Services;

public class PlaceResult
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class GooglePlacesService
{
    private readonly HttpClient _httpClient;
    private const string ApiKey = "AIzaSyCU3RmTPIWzJMwPGaM46m7TwZObPY26alY";
    private const string PlacesApiUrl = "https://maps.googleapis.com/maps/api/place";

    public GooglePlacesService()
    {
        _httpClient = new HttpClient();
    }

    public async Task<List<PlaceResult>> SearchPlacesAsync(string query, double latitude = 7.0731, double longitude = 125.6128)
    {
        try
        {
            var url = $"{PlacesApiUrl}/textsearch/json?query={Uri.EscapeDataString(query)}&location={latitude},{longitude}&radius=50000&key={ApiKey}";
            
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<GooglePlacesResponse>(response);

            return result?.Results?.Take(8).Select(r => new PlaceResult
            {
                Name = r.Name ?? "Unknown",
                Address = r.FormattedAddress ?? "No address",
                Latitude = r.Geometry?.Location?.Lat ?? 0,
                Longitude = r.Geometry?.Location?.Lng ?? 0
            }).ToList() ?? new List<PlaceResult>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error searching places: {ex.Message}");
            return new List<PlaceResult>();
        }
    }

    public async Task<List<PlaceResult>> GetPlacePredictionsAsync(string input, double latitude = 7.0731, double longitude = 125.6128)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<PlaceResult>();

            var url = $"{PlacesApiUrl}/autocomplete/json?input={Uri.EscapeDataString(input)}&location={latitude},{longitude}&radius=50000&key={ApiKey}";
            
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"API request failed: {response.StatusCode}");
                return new List<PlaceResult>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<GooglePlacesPredictionsResponse>(content);

            var predictions = new List<PlaceResult>();
            
            if (result?.Predictions != null)
            {
                // Limit concurrent requests to avoid overwhelming the API
                var semaphore = new SemaphoreSlim(3, 3);
                var tasks = result.Predictions.Take(5).Select(async prediction =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        return await GetPlaceDetailsAsync(prediction.PlaceId);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks);
                predictions.AddRange(results.Where(r => r != null)!);
            }

            return predictions;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting predictions: {ex.Message}");
            return new List<PlaceResult>();
        }
    }

    private async Task<PlaceResult?> GetPlaceDetailsAsync(string placeId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(placeId))
                return null;

            var url = $"{PlacesApiUrl}/details/json?place_id={placeId}&fields=name,formatted_address,geometry&key={ApiKey}";
            
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"Place details request failed: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<GooglePlaceDetailsResponse>(content);

            if (result?.Result != null)
            {
                return new PlaceResult
                {
                    Name = result.Result.Name ?? "Unknown",
                    Address = result.Result.FormattedAddress ?? "No address",
                    Latitude = result.Result.Geometry?.Location?.Lat ?? 0,
                    Longitude = result.Result.Geometry?.Location?.Lng ?? 0
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting place details: {ex.Message}");
            return null;
        }
    }

    public async Task<List<Location>> GetDirectionsRouteAsync(Location origin, Location destination)
    {
        try
        {
            var url = $"https://maps.googleapis.com/maps/api/directions/json?" +
                     $"origin={origin.Latitude},{origin.Longitude}&" +
                     $"destination={destination.Latitude},{destination.Longitude}&" +
                     $"mode=driving&" +
                     $"key={ApiKey}";

            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"Directions API request failed: {response.StatusCode}");
                return new List<Location> { origin, destination };
            }

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<GoogleDirectionsResponse>(content);

            if (result?.Routes?.Any() == true)
            {
                var route = result.Routes.First();
                var points = new List<Location>();

                foreach (var leg in route.Legs)
                {
                    foreach (var step in leg.Steps)
                    {
                        var decodedPoints = DecodePolyline(step.Polyline.Points);
                        points.AddRange(decodedPoints);
                    }
                }

                return points.Any() ? points : new List<Location> { origin, destination };
            }

            return new List<Location> { origin, destination };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting directions: {ex.Message}");
            return new List<Location> { origin, destination };
        }
    }

    private List<Location> DecodePolyline(string encoded)
    {
        var points = new List<Location>();
        if (string.IsNullOrEmpty(encoded)) return points;

        int index = 0, len = encoded.Length;
        int lat = 0, lng = 0;

        while (index < len)
        {
            int b, shift = 0, result = 0;
            do
            {
                b = encoded[index++] - 63;
                result |= (b & 0x1f) << shift;
                shift += 5;
            } while (b >= 0x20);
            int dlat = ((result & 1) != 0 ? ~(result >> 1) : (result >> 1));
            lat += dlat;

            shift = 0;
            result = 0;
            do
            {
                b = encoded[index++] - 63;
                result |= (b & 0x1f) << shift;
                shift += 5;
            } while (b >= 0x20);
            int dlng = ((result & 1) != 0 ? ~(result >> 1) : (result >> 1));
            lng += dlng;

            points.Add(new Location(lat / 1E5, lng / 1E5));
        }

        return points;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

// Response models for Google Places API
public class GooglePlacesResponse
{
    [JsonProperty("results")]
    public List<GooglePlace>? Results { get; set; }
}

public class GooglePlacesPredictionsResponse
{
    [JsonProperty("predictions")]
    public List<GooglePlacePrediction>? Predictions { get; set; }
}

public class GooglePlaceDetailsResponse
{
    [JsonProperty("result")]
    public GooglePlace? Result { get; set; }
}

public class GooglePlace
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("formatted_address")]
    public string? FormattedAddress { get; set; }

    [JsonProperty("geometry")]
    public GoogleGeometry? Geometry { get; set; }
}

public class GooglePlacePrediction
{
    [JsonProperty("place_id")]
    public string PlaceId { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string? Description { get; set; }
}

public class GoogleGeometry
{
    [JsonProperty("location")]
    public GoogleLocation? Location { get; set; }
}

public class GoogleLocation
{
    [JsonProperty("lat")]
    public double Lat { get; set; }

    [JsonProperty("lng")]
    public double Lng { get; set; }
}

// Google Directions API Models
public class GoogleDirectionsResponse
{
    [JsonProperty("routes")]
    public List<GoogleDirectionsRoute>? Routes { get; set; }

    [JsonProperty("status")]
    public string? Status { get; set; }
}

public class GoogleDirectionsRoute
{
    [JsonProperty("legs")]
    public List<GoogleDirectionsLeg> Legs { get; set; } = new();

    [JsonProperty("overview_polyline")]
    public GooglePolyline? OverviewPolyline { get; set; }
}

public class GoogleDirectionsLeg
{
    [JsonProperty("steps")]
    public List<GoogleDirectionsStep> Steps { get; set; } = new();

    [JsonProperty("distance")]
    public GoogleDistance? Distance { get; set; }

    [JsonProperty("duration")]
    public GoogleDuration? Duration { get; set; }
}

public class GoogleDirectionsStep
{
    [JsonProperty("polyline")]
    public GooglePolyline Polyline { get; set; } = new();

    [JsonProperty("distance")]
    public GoogleDistance? Distance { get; set; }

    [JsonProperty("duration")]
    public GoogleDuration? Duration { get; set; }
}

public class GooglePolyline
{
    [JsonProperty("points")]
    public string Points { get; set; } = string.Empty;
}

public class GoogleDistance
{
    [JsonProperty("text")]
    public string? Text { get; set; }

    [JsonProperty("value")]
    public int Value { get; set; }
}

public class GoogleDuration
{
    [JsonProperty("text")]
    public string? Text { get; set; }

    [JsonProperty("value")]
    public int Value { get; set; }
}
