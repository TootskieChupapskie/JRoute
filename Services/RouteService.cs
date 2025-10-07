using JRoute.Models;
using Microsoft.Maui.Maps;
using Supabase;
using Newtonsoft.Json;
using Microsoft.Extensions.DependencyInjection;

namespace JRoute.Services;

public class RouteService
{
    private readonly IGeoJsonProvider _geoProvider;
    private readonly GooglePlacesService _googlePlacesService;
    private List<JeepneyRoute> _cachedRoutes = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(30);
    private const string StorageBucket = "Jroute";
    private const string StoragePrefix = "routes/";

    // Available routes in the system (same as Conductor)
    private readonly List<string> _availableRoutes = new()
    {
        "Bago Aplaya", "Bangkal", "Barrio Obrero", "Buhangin Via Dacudao", "Buhangin Via JP. Laurel", 
        "Bunawan Via Buhangin", "Bunawan Via Sasa", "Calinan", "Camp Catitipan Via JP. Laurel", 
        "Catalunan Grande", "Ecoland", "El Rio", "Toril", "Maa-Agdao"
    };

    public RouteService(IGeoJsonProvider geoProvider)
    {
        _geoProvider = geoProvider;
        _googlePlacesService = new GooglePlacesService();
    }

    public async Task<List<JeepneyRoute>> GetAllRoutesAsync()
    {
        try
        {
            // Use cache if still valid
            if (_cachedRoutes.Any() && DateTime.Now - _lastCacheUpdate < _cacheExpiry)
            {
                System.Diagnostics.Debug.WriteLine($"Using cached routes: {_cachedRoutes.Count} routes");
                return _cachedRoutes;
            }

            System.Diagnostics.Debug.WriteLine("Fetching routes from Supabase Storage...");
            _cachedRoutes.Clear();

            // Load each available route from GeoJSON files
            foreach (var routeName in _availableRoutes)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"Attempting to load route: {routeName}");
                    var route = await LoadRouteFromGeoJsonAsync(routeName);
                    if (route != null && route.Points.Any())
                    {
                        _cachedRoutes.Add(route);
                        System.Diagnostics.Debug.WriteLine($"✓ Successfully loaded route: {route.Name} with {route.Points.Count} points");
                        
                        // Show first and last points for debugging
                        var firstPoint = route.Points.First();
                        var lastPoint = route.Points.Last();
                        System.Diagnostics.Debug.WriteLine($"  Route spans from {firstPoint.Latitude:F4},{firstPoint.Longitude:F4} to {lastPoint.Latitude:F4},{lastPoint.Longitude:F4}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Route {routeName} returned null or no points");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✗ Failed to load route {routeName}: {ex.Message}");
                }
            }

            // If no routes loaded from storage, add sample routes for testing
            if (!_cachedRoutes.Any())
            {
                System.Diagnostics.Debug.WriteLine("No routes loaded from storage, adding sample routes for testing");
                _cachedRoutes = GetSampleRoutes();
            }
            
            _lastCacheUpdate = DateTime.Now;

            System.Diagnostics.Debug.WriteLine($"Total loaded routes: {_cachedRoutes.Count}");
            foreach (var route in _cachedRoutes)
            {
                System.Diagnostics.Debug.WriteLine($"Route: {route.Name} with {route.Points.Count} points");
            }

            return _cachedRoutes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching routes: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            
            // Fallback to sample routes if everything fails
            if (!_cachedRoutes.Any())
            {
                _cachedRoutes = GetSampleRoutes();
            }
            
            return _cachedRoutes;
        }
    }

    private async Task<JeepneyRoute?> LoadRouteFromGeoJsonAsync(string routeName)
    {
        try
        {
            var slug = Slugify(routeName);
            var path = $"{StoragePrefix}{slug}.geojson";
            
            System.Diagnostics.Debug.WriteLine($"Loading route from: {path}");
            var geoJsonContent = await _geoProvider.GetAsync(StorageBucket, path);
            
            if (string.IsNullOrEmpty(geoJsonContent))
            {
                System.Diagnostics.Debug.WriteLine($"Empty GeoJSON content for {routeName}");
                return null;
            }

            // Parse GeoJSON and extract coordinates
            var points = ParseGeoJsonToRoutePoints(geoJsonContent);
            if (!points.Any())
            {
                System.Diagnostics.Debug.WriteLine($"No points extracted from GeoJSON for {routeName}");
                return null;
            }

            return new JeepneyRoute
            {
                Id = slug,
                Name = routeName,
                Description = $"{routeName} route",
                Points = points,
                Fare = GetEstimatedFare(routeName),
                VehicleType = "Jeepney",
                EstimatedDuration = TimeSpan.FromMinutes(GetEstimatedDuration(routeName))
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading route {routeName}: {ex.Message}");
            return null;
        }
    }

    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim().ToLowerInvariant();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[^\p{L}\p{Nd}\s_-]", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[\s_]+", "-");
        t = System.Text.RegularExpressions.Regex.Replace(t, "-{2,}", "-").Trim('-');
        return t;
    }

    private List<RoutePoint> ParseGeoJsonToRoutePoints(string geoJsonContent)
    {
        try
        {
            var geoJson = JsonConvert.DeserializeObject<dynamic>(geoJsonContent);
            var points = new List<RoutePoint>();

            if (geoJson?.features != null)
            {
                foreach (var feature in geoJson.features)
                {
                    if (feature?.geometry?.type == "LineString" && feature.geometry.coordinates != null)
                    {
                        var coordinates = feature.geometry.coordinates;
                        for (int i = 0; i < coordinates.Count; i++)
                        {
                            var coord = coordinates[i];
                            if (coord.Count >= 2)
                            {
                                points.Add(new RoutePoint
                                {
                                    Longitude = (double)coord[0],
                                    Latitude = (double)coord[1],
                                    Name = $"Point {i + 1}"
                                });
                            }
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"Parsed {points.Count} points from GeoJSON");
            return points;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error parsing GeoJSON: {ex.Message}");
            return new List<RoutePoint>();
        }
    }

    private double GetEstimatedFare(string routeName)
    {
        // Estimate fare based on route name/distance
        return routeName.ToLower() switch
        {
            var name when name.Contains("toril") => 20.0,
            var name when name.Contains("calinan") => 25.0,
            var name when name.Contains("bunawan") => 18.0,
            var name when name.Contains("catalunan") => 16.0,
            _ => 15.0
        };
    }

    private int GetEstimatedDuration(string routeName)
    {
        // Estimate duration based on route name/distance
        return routeName.ToLower() switch
        {
            var name when name.Contains("toril") => 60,
            var name when name.Contains("calinan") => 70,
            var name when name.Contains("bunawan") => 50,
            var name when name.Contains("catalunan") => 45,
            _ => 40
        };
    }

    public async Task<ComputedRoute> FindOptimalRouteAsync(Location startLocation, Location endLocation)
    {
        System.Diagnostics.Debug.WriteLine("=== ROUTE OPTIMIZATION ALGORITHM ===");
        
        // Step 1: Get the initial optimized route using Google Directions API (the "base paper")
        System.Diagnostics.Debug.WriteLine("Step 1: Creating initial optimized route using Google Directions API...");
        var initialRoute = await _googlePlacesService.GetDirectionsRouteAsync(startLocation, endLocation);
        
        if (!initialRoute.Any())
        {
            System.Diagnostics.Debug.WriteLine("Failed to get initial route from Google Directions API");
            initialRoute = new List<Location> { startLocation, endLocation };
        }
        
        System.Diagnostics.Debug.WriteLine($"Initial route has {initialRoute.Count} points spanning {CalculateTotalDistance(initialRoute):F0}m");
        
        // Step 2: Load all available routes from database (all the "route papers")
        System.Diagnostics.Debug.WriteLine("Step 2: Loading all available routes from database...");
        var allRoutes = await GetAllRoutesAsync();
        System.Diagnostics.Debug.WriteLine($"Loaded {allRoutes.Count} total routes");
        
        // Step 3: Filter routes that intersect/overlap with initial route (keep relevant "papers")
        System.Diagnostics.Debug.WriteLine("Step 3: Filtering routes that overlap with initial route...");
        var overlappingRoutes = FindRoutesOverlappingWithInitial(initialRoute, allRoutes);
        System.Diagnostics.Debug.WriteLine($"Found {overlappingRoutes.Count} routes that overlap with initial route");
        
        // Step 4: Splice and optimize those routes to approximate the initial route
        System.Diagnostics.Debug.WriteLine("Step 4: Splicing and optimizing overlapping routes...");
        var optimizedRoute = SpliceAndOptimizeRoutes(startLocation, endLocation, initialRoute, overlappingRoutes);
        
        System.Diagnostics.Debug.WriteLine($"=== FINAL RESULT: {optimizedRoute.Segments.Count} segments, {optimizedRoute.TransferCount} transfers ===");
        return optimizedRoute;
    }

    private List<JeepneyRoute> FindRoutesOverlappingWithInitial(List<Location> initialRoute, List<JeepneyRoute> allRoutes)
    {
        var overlappingRoutes = new List<JeepneyRoute>();
        const double overlapThreshold = 300; // 300m threshold for considering overlap
        
        foreach (var jeepneyRoute in allRoutes)
        {
            var overlapCount = 0;
            var totalPoints = jeepneyRoute.Points.Count;
            
            // Check how many points of the jeepney route are close to the initial route
            foreach (var jeepneyPoint in jeepneyRoute.Points)
            {
                var minDistanceToInitial = initialRoute.Min(initialPoint => 
                    CalculateDistance(jeepneyPoint.Location, initialPoint));
                
                if (minDistanceToInitial <= overlapThreshold)
                {
                    overlapCount++;
                }
            }
            
            // If at least 20% of the jeepney route overlaps with initial route, keep it
            var overlapPercentage = (double)overlapCount / totalPoints;
            if (overlapPercentage >= 0.2)
            {
                overlappingRoutes.Add(jeepneyRoute);
                System.Diagnostics.Debug.WriteLine($"✓ Route '{jeepneyRoute.Name}' overlaps {overlapPercentage:P1} with initial route");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"✗ Route '{jeepneyRoute.Name}' only overlaps {overlapPercentage:P1} - discarded");
            }
        }
        
        return overlappingRoutes;
    }

    private ComputedRoute SpliceAndOptimizeRoutes(Location start, Location end, List<Location> initialRoute, List<JeepneyRoute> overlappingRoutes)
    {
        var computedRoute = new ComputedRoute();
        
        if (!overlappingRoutes.Any())
        {
            System.Diagnostics.Debug.WriteLine("No overlapping routes found - creating walking route along initial path");
            // Create walking route along initial route
            computedRoute.WalkingSegments = initialRoute.Select((loc, index) => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = $"Walk Point {index + 1}"
            }).ToList();
            
            computedRoute.TotalDistance = CalculateTotalDistance(initialRoute);
            return computedRoute;
        }
        
        // Find the best combination of overlapping routes that covers the initial route
        var routeCombination = FindBestRouteCombination(start, end, initialRoute, overlappingRoutes);
        
        if (routeCombination.Any())
        {
            // Create segments from the route combination
            foreach (var routeMatch in routeCombination)
            {
                computedRoute.Segments.Add(CreateRouteSegment(routeMatch));
            }
            
            // Add walking segments using initial route for connections
            AddWalkingSegmentsFromInitialRoute(computedRoute, start, end, initialRoute, routeCombination);
            
            // Calculate totals
            computedRoute.TotalFare = computedRoute.Segments.Sum(s => s.Fare);
            computedRoute.TotalDuration = TimeSpan.FromMinutes(computedRoute.Segments.Sum(s => s.Duration.TotalMinutes));
            computedRoute.TotalDistance = computedRoute.Segments.Sum(s => s.Distance);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("No viable route combination found - falling back to walking route");
            // Fallback to walking route
            computedRoute.WalkingSegments = initialRoute.Select((loc, index) => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = $"Walk Point {index + 1}"
            }).ToList();
            
            computedRoute.TotalDistance = CalculateTotalDistance(initialRoute);
        }
        
        return computedRoute;
    }

    private List<RouteMatch> FindBestRouteCombination(Location start, Location end, List<Location> initialRoute, List<JeepneyRoute> overlappingRoutes)
    {
        var bestCombination = new List<RouteMatch>();
        var currentLocation = start;
        var usedRoutes = new HashSet<string>();
        var maxSegments = 4; // Allow up to 4 route segments
        
        System.Diagnostics.Debug.WriteLine($"Finding best route combination from {overlappingRoutes.Count} overlapping routes");
        
        for (int segment = 0; segment < maxSegments; segment++)
        {
            var bestMatch = FindBestNextRoute(currentLocation, end, initialRoute, overlappingRoutes, usedRoutes);
            
            if (bestMatch == null)
            {
                System.Diagnostics.Debug.WriteLine($"No more suitable routes found at segment {segment}");
                break;
            }
            
            bestCombination.Add(bestMatch);
            usedRoutes.Add(bestMatch.Route.Id);
            currentLocation = bestMatch.NearestDropPoint.Location;
            
            System.Diagnostics.Debug.WriteLine($"Added segment {segment + 1}: {bestMatch.Route.Name}");
            
            // Check if we're close enough to destination
            var distanceToEnd = CalculateDistance(currentLocation, end);
            if (distanceToEnd <= 500) // 500m threshold
            {
                System.Diagnostics.Debug.WriteLine($"Close enough to destination ({distanceToEnd:F0}m) - stopping");
                break;
            }
        }
        
        return bestCombination;
    }

    private RouteMatch? FindBestNextRoute(Location currentLocation, Location destination, List<Location> initialRoute, List<JeepneyRoute> routes, HashSet<string> usedRoutes)
    {
        RouteMatch? bestMatch = null;
        double bestScore = 0;
        
        foreach (var route in routes.Where(r => !usedRoutes.Contains(r.Id)))
        {
            var pickupPoint = FindNearestPoint(currentLocation, route.Points, out int pickupIndex, out double pickupDistance);
            var dropPoint = FindNearestPoint(destination, route.Points, out int dropIndex, out double dropDistance);
            
            // Only consider routes where pickup comes before drop and distances are reasonable
            if (pickupIndex >= dropIndex || pickupDistance > 1000 || dropDistance > 1000)
                continue;
            
            // Calculate score based on:
            // 1. How close pickup is to current location
            // 2. How close drop is to destination  
            // 3. How well the route segment follows the initial route
            var routeSegmentScore = CalculateRouteSegmentScore(route, pickupIndex, dropIndex, initialRoute);
            var proximityScore = 1000 - pickupDistance; // Closer pickup = higher score
            var destinationScore = 1000 - dropDistance; // Closer to destination = higher score
            
            var totalScore = routeSegmentScore + (proximityScore * 0.3) + (destinationScore * 0.5);
            
            if (totalScore > bestScore)
            {
                bestScore = totalScore;
                bestMatch = new RouteMatch
                {
                    Route = route,
                    NearestPickupPoint = pickupPoint,
                    NearestDropPoint = dropPoint,
                    PickupDistance = pickupDistance,
                    DropDistance = dropDistance,
                    RouteOverlap = routeSegmentScore / 1000.0, // Normalize to 0-1
                    PickupIndex = pickupIndex,
                    DropIndex = dropIndex
                };
            }
        }
        
        return bestMatch;
    }

    private double CalculateRouteSegmentScore(JeepneyRoute route, int startIndex, int endIndex, List<Location> initialRoute)
    {
        double score = 0;
        int pointsChecked = 0;
        
        // Check how well the route segment aligns with the initial route
        for (int i = startIndex; i <= endIndex && i < route.Points.Count; i++)
        {
            var routePoint = route.Points[i].Location;
            var minDistanceToInitial = initialRoute.Min(initialPoint => 
                CalculateDistance(routePoint, initialPoint));
            
            // Closer to initial route = higher score
            var pointScore = Math.Max(0, 500 - minDistanceToInitial); // Max 500 points per point
            score += pointScore;
            pointsChecked++;
        }
        
        return pointsChecked > 0 ? score / pointsChecked : 0;
    }


    private void AddWalkingSegmentsFromInitialRoute(ComputedRoute computedRoute, Location start, Location end, List<Location> initialRoute, List<RouteMatch> routeCombination)
    {
        if (!routeCombination.Any()) return;
        
        // Add walking from start to first pickup using initial route
        var firstPickup = routeCombination.First().NearestPickupPoint.Location;
        var walkingToFirst = ExtractInitialRouteSegment(initialRoute, start, firstPickup);
        AddWalkingSegment(computedRoute, walkingToFirst, "Walk to pickup");
        
        // Add walking between route segments
        for (int i = 0; i < routeCombination.Count - 1; i++)
        {
            var currentDrop = routeCombination[i].NearestDropPoint.Location;
            var nextPickup = routeCombination[i + 1].NearestPickupPoint.Location;
            var walkingBetween = ExtractInitialRouteSegment(initialRoute, currentDrop, nextPickup);
            AddWalkingSegment(computedRoute, walkingBetween, "Walk to transfer");
        }
        
        // Add walking from last drop to destination using initial route
        var lastDrop = routeCombination.Last().NearestDropPoint.Location;
        var walkingToEnd = ExtractInitialRouteSegment(initialRoute, lastDrop, end);
        AddWalkingSegment(computedRoute, walkingToEnd, "Walk to destination");
    }

    private List<Location> ExtractInitialRouteSegment(List<Location> initialRoute, Location start, Location end)
    {
        if (!initialRoute.Any()) return new List<Location> { start, end };
        
        var startIndex = FindClosestPointIndex(initialRoute, start);
        var endIndex = FindClosestPointIndex(initialRoute, end);
        
        if (startIndex > endIndex)
            (startIndex, endIndex) = (endIndex, startIndex);
        
        var segment = new List<Location>();
        for (int i = startIndex; i <= endIndex && i < initialRoute.Count; i++)
        {
            segment.Add(initialRoute[i]);
        }
        
        return segment.Any() ? segment : new List<Location> { start, end };
    }

    private void AddWalkingSegment(ComputedRoute computedRoute, List<Location> walkingPath, string segmentName)
    {
        if (walkingPath.Any())
        {
            computedRoute.WalkingSegments.AddRange(walkingPath.Select((loc, index) => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = $"{segmentName} {index + 1}"
            }));
        }
    }

    private List<RouteMatch> FindAllPossibleRoutes(Location start, Location end, List<JeepneyRoute> routes)
    {
        var matches = new List<RouteMatch>();
        const double maxWalkingDistance = 2000; // 2km max walking to pickup/drop points (increased for better coverage)

        System.Diagnostics.Debug.WriteLine($"Searching for routes from {start.Latitude:F4},{start.Longitude:F4} to {end.Latitude:F4},{end.Longitude:F4}");
        System.Diagnostics.Debug.WriteLine($"Available routes: {routes.Count}");

        foreach (var route in routes)
        {
            if (!route.Points.Any())
            {
                System.Diagnostics.Debug.WriteLine($"Route {route.Name} has no points, skipping");
                continue;
            }

            var pickupPoint = FindNearestPoint(start, route.Points, out int pickupIndex, out double pickupDistance);
            var dropPoint = FindNearestPoint(end, route.Points, out int dropIndex, out double dropDistance);

            System.Diagnostics.Debug.WriteLine($"Route {route.Name}: Pickup at index {pickupIndex} ({pickupDistance:F0}m), Drop at index {dropIndex} ({dropDistance:F0}m)");

            // Accept routes where:
            // 1. Pickup comes before drop (correct direction)
            // 2. Walking distances are reasonable
            if (pickupIndex < dropIndex && pickupDistance <= maxWalkingDistance && dropDistance <= maxWalkingDistance)
            {
                // Calculate how much this route helps (distance saved)
                var directDistance = CalculateDistance(start, end);
                var routeDistance = CalculateRouteDistance(route, pickupIndex, dropIndex);
                var totalWalkingDistance = pickupDistance + dropDistance;
                
                // Route efficiency: how much closer it gets us to destination
                var efficiency = Math.Max(0, directDistance - totalWalkingDistance) / directDistance;
                
                matches.Add(new RouteMatch
                {
                    Route = route,
                    NearestPickupPoint = pickupPoint,
                    NearestDropPoint = dropPoint,
                    PickupDistance = pickupDistance,
                    DropDistance = dropDistance,
                    RouteOverlap = efficiency, // Use efficiency as overlap score
                    PickupIndex = pickupIndex,
                    DropIndex = dropIndex
                });

                System.Diagnostics.Debug.WriteLine($"✓ Route {route.Name} ACCEPTED: Pickup {pickupDistance:F0}m, Drop {dropDistance:F0}m, Efficiency {efficiency:P1}");
            }
            else
            {
                var reason = pickupIndex >= dropIndex ? "wrong direction" : "too far to walk";
                System.Diagnostics.Debug.WriteLine($"✗ Route {route.Name} REJECTED: {reason}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"Found {matches.Count} matching routes");
        return matches.OrderByDescending(m => m.RouteOverlap).ToList();
    }

    private List<JeepneyRoute> FindOverlappingRoutes(List<Location> baselineRoute, List<JeepneyRoute> allRoutes)
    {
        var overlappingRoutes = new List<JeepneyRoute>();
        const double overlapThreshold = 200; // 200 meters threshold for overlap

        foreach (var jeepneyRoute in allRoutes)
        {
            var overlapCount = 0;
            var totalChecked = 0;

            // Check how many points of the jeepney route are close to the baseline route
            foreach (var jeepneyPoint in jeepneyRoute.Points)
            {
                totalChecked++;
                var minDistance = baselineRoute.Min(basePoint => 
                    CalculateDistance(jeepneyPoint.Location, basePoint));

                if (minDistance <= overlapThreshold)
                {
                    overlapCount++;
                }
            }

            // If at least 30% of the jeepney route overlaps with baseline, include it
            var overlapPercentage = (double)overlapCount / totalChecked;
            if (overlapPercentage >= 0.3)
            {
                overlappingRoutes.Add(jeepneyRoute);
                System.Diagnostics.Debug.WriteLine($"Route {jeepneyRoute.Name} overlaps {overlapPercentage:P1} with baseline");
            }
        }

        return overlappingRoutes;
    }

    private ComputedRoute ComputeOptimalRouteWithBaseline(Location start, Location end, List<Location> baselineRoute, List<RouteMatch> routeMatches)
    {
        var computedRoute = new ComputedRoute();

        if (!routeMatches.Any())
        {
            // No vehicle routes found - return walking route along baseline
            computedRoute.WalkingSegments = baselineRoute.Select(loc => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = "Walking"
            }).ToList();
            
            computedRoute.TotalDistance = CalculateTotalDistance(baselineRoute);
            return computedRoute;
        }

        // Try single route first (if it's efficient enough)
        var bestSingleRoute = routeMatches.First();
        if (bestSingleRoute.RouteOverlap > 0.3) // 30% efficiency threshold for single route
        {
            computedRoute.Segments.Add(CreateRouteSegment(bestSingleRoute));
            computedRoute.TotalFare = bestSingleRoute.Route.Fare;
            computedRoute.TotalDuration = bestSingleRoute.Route.EstimatedDuration;
            
            // Add walking segments from baseline route
            AddWalkingSegmentsFromBaseline(computedRoute, start, end, baselineRoute, bestSingleRoute);
        }
        else
        {
            // Multi-route solution - find combination that gets closest to destination
            computedRoute = FindMultiRouteConnectionWithBaseline(start, end, baselineRoute, routeMatches);
        }

        return computedRoute;
    }

    private List<RouteMatch> FindMatchingRoutesFromOverlapping(Location start, Location end, List<JeepneyRoute> routes)
    {
        var matches = new List<RouteMatch>();

        foreach (var route in routes)
        {
            var pickupPoint = FindNearestPoint(start, route.Points, out int pickupIndex, out double pickupDistance);
            var dropPoint = FindNearestPoint(end, route.Points, out int dropIndex, out double dropDistance);

            // Only consider routes where pickup comes before drop
            if (pickupIndex < dropIndex && pickupDistance <= 500 && dropDistance <= 500) // 500m max walking
            {
                var overlap = CalculateRouteOverlap(start, end, route, pickupIndex, dropIndex);
                
                matches.Add(new RouteMatch
                {
                    Route = route,
                    NearestPickupPoint = pickupPoint,
                    NearestDropPoint = dropPoint,
                    PickupDistance = pickupDistance,
                    DropDistance = dropDistance,
                    RouteOverlap = overlap,
                    PickupIndex = pickupIndex,
                    DropIndex = dropIndex
                });
            }
        }

        return matches.OrderByDescending(m => m.RouteOverlap).ToList();
    }

    private void AddWalkingSegmentsFromBaseline(ComputedRoute computedRoute, Location start, Location end, List<Location> baselineRoute, RouteMatch routeMatch)
    {
        // Add walking segment from start to pickup point using baseline route
        var pickupLocation = routeMatch.NearestPickupPoint.Location;
        var walkingToPickup = ExtractBaselineSegment(baselineRoute, start, pickupLocation);
        
        if (walkingToPickup.Any())
        {
            computedRoute.WalkingSegments.AddRange(walkingToPickup.Select(loc => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = "Walk to pickup"
            }));
        }

        // Add walking segment from drop point to destination using baseline route
        var dropLocation = routeMatch.NearestDropPoint.Location;
        var walkingFromDrop = ExtractBaselineSegment(baselineRoute, dropLocation, end);
        
        if (walkingFromDrop.Any())
        {
            computedRoute.WalkingSegments.AddRange(walkingFromDrop.Select(loc => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = "Walk from drop"
            }));
        }
    }

    private List<Location> ExtractBaselineSegment(List<Location> baselineRoute, Location start, Location end)
    {
        if (!baselineRoute.Any()) return new List<Location>();

        // Find closest points on baseline route to start and end
        var startIndex = FindClosestPointIndex(baselineRoute, start);
        var endIndex = FindClosestPointIndex(baselineRoute, end);

        // Ensure proper order
        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        // Extract segment
        var segment = new List<Location>();
        for (int i = startIndex; i <= endIndex && i < baselineRoute.Count; i++)
        {
            segment.Add(baselineRoute[i]);
        }

        return segment;
    }

    private int FindClosestPointIndex(List<Location> route, Location target)
    {
        var closestIndex = 0;
        var minDistance = CalculateDistance(route[0], target);

        for (int i = 1; i < route.Count; i++)
        {
            var distance = CalculateDistance(route[i], target);
            if (distance < minDistance)
            {
                minDistance = distance;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    private ComputedRoute FindMultiRouteConnectionWithBaseline(Location start, Location end, List<Location> baselineRoute, List<RouteMatch> matches)
    {
        var computedRoute = new ComputedRoute();
        var currentLocation = start;
        var usedRoutes = new HashSet<string>();
        var maxTransfers = 3;
        var maxWalkingToPickup = 1500; // 1.5km max walking to pickup (increased for better coverage)

        System.Diagnostics.Debug.WriteLine($"Starting multi-route search from {start.Latitude:F4},{start.Longitude:F4} to {end.Latitude:F4},{end.Longitude:F4}");

        for (int transfer = 0; transfer < maxTransfers; transfer++)
        {
            // Find the best next route from current location
            var availableRoutes = matches
                .Where(m => !usedRoutes.Contains(m.Route.Id))
                .Where(m => CalculateDistance(currentLocation, m.NearestPickupPoint.Location) <= maxWalkingToPickup)
                .OrderBy(m => CalculateDistance(currentLocation, m.NearestPickupPoint.Location)) // Closest pickup first
                .ThenBy(m => CalculateDistance(m.NearestDropPoint.Location, end)) // Then closest to destination
                .ToList();

            if (!availableRoutes.Any())
            {
                System.Diagnostics.Debug.WriteLine($"No more available routes from current location");
                break;
            }

            var bestRoute = availableRoutes.First();
            System.Diagnostics.Debug.WriteLine($"Adding route {bestRoute.Route.Name}, pickup {CalculateDistance(currentLocation, bestRoute.NearestPickupPoint.Location):F0}m away");

            // Add walking segment to pickup using baseline route
            if (CalculateDistance(currentLocation, bestRoute.NearestPickupPoint.Location) > 50)
            {
                var walkingToPickup = ExtractBaselineSegment(baselineRoute, currentLocation, bestRoute.NearestPickupPoint.Location);
                if (walkingToPickup.Any())
                {
                    computedRoute.WalkingSegments.AddRange(walkingToPickup.Take(100).Select(loc => new RoutePoint
                    {
                        Latitude = loc.Latitude,
                        Longitude = loc.Longitude,
                        Name = transfer == 0 ? "Walk to pickup" : "Walk to transfer"
                    }));
                }
            }

            // Add the route segment
            computedRoute.Segments.Add(CreateRouteSegment(bestRoute));
            usedRoutes.Add(bestRoute.Route.Id);
            currentLocation = bestRoute.NearestDropPoint.Location;

            System.Diagnostics.Debug.WriteLine($"Now at {currentLocation.Latitude:F4},{currentLocation.Longitude:F4}, {CalculateDistance(currentLocation, end):F0}m from destination");

            // Check if we're close enough to destination
            if (CalculateDistance(currentLocation, end) <= 500)
            {
                System.Diagnostics.Debug.WriteLine($"Close enough to destination, stopping route search");
                break;
            }
        }

        // Add final walking segment to destination using baseline route
        if (CalculateDistance(currentLocation, end) > 50)
        {
            var walkingToDestination = ExtractBaselineSegment(baselineRoute, currentLocation, end);
            if (walkingToDestination.Any())
            {
                computedRoute.WalkingSegments.AddRange(walkingToDestination.Take(100).Select(loc => new RoutePoint
                {
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude,
                    Name = "Walk to destination"
                }));
            }
        }

        // Calculate totals
        computedRoute.TotalFare = computedRoute.Segments.Sum(s => s.Fare);
        computedRoute.TotalDuration = TimeSpan.FromMinutes(computedRoute.Segments.Sum(s => s.Duration.TotalMinutes) + (computedRoute.WalkingSegments.Count * 0.75)); // Add walking time
        computedRoute.TotalDistance = computedRoute.Segments.Sum(s => s.Distance) + CalculateTotalDistance(computedRoute.WalkingSegments.Select(w => w.Location).ToList());

        System.Diagnostics.Debug.WriteLine($"Final route: {computedRoute.Segments.Count} segments, {computedRoute.TransferCount} transfers, ₱{computedRoute.TotalFare:F2}");

        return computedRoute;
    }

    private double CalculateTotalDistance(List<Location> route)
    {
        double totalDistance = 0;
        for (int i = 0; i < route.Count - 1; i++)
        {
            totalDistance += CalculateDistance(route[i], route[i + 1]);
        }
        return totalDistance;
    }


    private ComputedRoute ComputeOptimalRoute(Location start, Location end, List<RouteMatch> matches)
    {
        var computedRoute = new ComputedRoute();

        if (!matches.Any())
        {
            // No direct routes found - create walking route
            computedRoute.WalkingSegments.Add(new RoutePoint 
            { 
                Latitude = start.Latitude, 
                Longitude = start.Longitude, 
                Name = "Start" 
            });
            computedRoute.WalkingSegments.Add(new RoutePoint 
            { 
                Latitude = end.Latitude, 
                Longitude = end.Longitude, 
                Name = "Destination" 
            });
            computedRoute.TotalDistance = CalculateDistance(start, end);
            return computedRoute;
        }

        // Try single route first
        var bestSingleRoute = matches.First();
        if (bestSingleRoute.RouteOverlap > 0.7) // 70% overlap threshold
        {
            computedRoute.Segments.Add(CreateRouteSegment(bestSingleRoute));
            computedRoute.TotalFare = bestSingleRoute.Route.Fare;
            computedRoute.TotalDuration = bestSingleRoute.Route.EstimatedDuration;
            return computedRoute;
        }

        // Find multi-route solution
        return FindMultiRouteConnection(start, end, matches);
    }

    private ComputedRoute FindMultiRouteConnection(Location start, Location end, List<RouteMatch> matches)
    {
        var computedRoute = new ComputedRoute();
        var currentLocation = start;
        var remainingDistance = CalculateDistance(start, end);
        var usedRoutes = new HashSet<string>();

        while (remainingDistance > 200) // 200m threshold
        {
            var bestMatch = matches
                .Where(m => !usedRoutes.Contains(m.Route.Id))
                .Where(m => CalculateDistance(currentLocation, m.NearestPickupPoint.Location) <= 300)
                .OrderBy(m => CalculateDistance(currentLocation, m.NearestPickupPoint.Location))
                .ThenByDescending(m => CalculateDistanceToDestination(m.NearestDropPoint.Location, end))
                .FirstOrDefault();

            if (bestMatch == null) break;

            computedRoute.Segments.Add(CreateRouteSegment(bestMatch));
            usedRoutes.Add(bestMatch.Route.Id);
            currentLocation = bestMatch.NearestDropPoint.Location;
            remainingDistance = CalculateDistance(currentLocation, end);
        }

        // Calculate totals
        computedRoute.TotalFare = computedRoute.Segments.Sum(s => s.Fare);
        computedRoute.TotalDuration = TimeSpan.FromMinutes(computedRoute.Segments.Sum(s => s.Duration.TotalMinutes));
        computedRoute.TotalDistance = computedRoute.Segments.Sum(s => s.Distance);

        return computedRoute;
    }

    private RouteSegment CreateRouteSegment(RouteMatch match)
    {
        return new RouteSegment
        {
            Route = match.Route,
            StartPoint = match.NearestPickupPoint,
            EndPoint = match.NearestDropPoint,
            StartIndex = match.PickupIndex,
            EndIndex = match.DropIndex,
            Distance = CalculateRouteDistance(match.Route, match.PickupIndex, match.DropIndex),
            Duration = match.Route.EstimatedDuration,
            Fare = match.Route.Fare
        };
    }

    private RoutePoint FindNearestPoint(Location target, List<RoutePoint> points, out int index, out double distance)
    {
        var nearest = points[0];
        index = 0;
        distance = CalculateDistance(target, nearest.Location);

        for (int i = 1; i < points.Count; i++)
        {
            var dist = CalculateDistance(target, points[i].Location);
            if (dist < distance)
            {
                distance = dist;
                nearest = points[i];
                index = i;
            }
        }

        return nearest;
    }

    private double CalculateRouteOverlap(Location start, Location end, JeepneyRoute route, int startIndex, int endIndex)
    {
        var directDistance = CalculateDistance(start, end);
        var routeDistance = CalculateRouteDistance(route, startIndex, endIndex);
        
        return Math.Min(directDistance / routeDistance, 1.0);
    }

    private double CalculateRouteDistance(JeepneyRoute route, int startIndex, int endIndex)
    {
        double totalDistance = 0;
        for (int i = startIndex; i < endIndex; i++)
        {
            totalDistance += CalculateDistance(route.Points[i].Location, route.Points[i + 1].Location);
        }
        return totalDistance;
    }

    private double CalculateDistance(Location point1, Location point2)
    {
        return Location.CalculateDistance(point1, point2, DistanceUnits.Kilometers) * 1000; // Convert to meters
    }

    private double CalculateDistanceToDestination(Location current, Location destination)
    {
        return CalculateDistance(destination, current); // Inverted for ordering
    }


    private List<JeepneyRoute> GetSampleRoutes()
    {
        System.Diagnostics.Debug.WriteLine("Creating sample routes for testing...");
        return new List<JeepneyRoute>
        {
            new JeepneyRoute
            {
                Id = "el-rio-route",
                Name = "El Rio Route",
                Description = "El Rio via Matina-Ecoland route",
                Fare = 15.0,
                VehicleType = "Jeepney",
                EstimatedDuration = TimeSpan.FromMinutes(45),
                Points = new List<RoutePoint>
                {
                    new RoutePoint { Latitude = 7.0644, Longitude = 125.5975, Name = "Bankerohan Terminal" },
                    new RoutePoint { Latitude = 7.0680, Longitude = 125.6020, Name = "Roxas Avenue" },
                    new RoutePoint { Latitude = 7.0720, Longitude = 125.6080, Name = "Davao Doctors Hospital" },
                    new RoutePoint { Latitude = 7.0760, Longitude = 125.6140, Name = "Matina Crossing" },
                    new RoutePoint { Latitude = 7.0800, Longitude = 125.6200, Name = "Matina Aplaya" },
                    new RoutePoint { Latitude = 7.0840, Longitude = 125.6260, Name = "Ecoland Terminal" },
                    new RoutePoint { Latitude = 7.0880, Longitude = 125.6320, Name = "El Rio Vista" },
                    new RoutePoint { Latitude = 7.0920, Longitude = 125.6380, Name = "Communal Buhangin" },
                    new RoutePoint { Latitude = 7.0960, Longitude = 125.6440, Name = "Buhangin Proper" },
                    new RoutePoint { Latitude = 7.1000, Longitude = 125.6500, Name = "Buhangin Extension" }
                }
            },
            new JeepneyRoute
            {
                Id = "maa-agdao-route",
                Name = "Maa-Agdao Route", 
                Description = "Maa to Agdao via downtown route",
                Fare = 18.0,
                VehicleType = "Jeepney",
                EstimatedDuration = TimeSpan.FromMinutes(50),
                Points = new List<RoutePoint>
                {
                    new RoutePoint { Latitude = 7.0400, Longitude = 125.5800, Name = "Maa Terminal" },
                    new RoutePoint { Latitude = 7.0450, Longitude = 125.5850, Name = "Maa Proper" },
                    new RoutePoint { Latitude = 7.0500, Longitude = 125.5900, Name = "Shrine Hills" },
                    new RoutePoint { Latitude = 7.0550, Longitude = 125.5950, Name = "Matina Pangi" },
                    new RoutePoint { Latitude = 7.0600, Longitude = 125.6000, Name = "Matina Crossing" },
                    new RoutePoint { Latitude = 7.0644, Longitude = 125.5975, Name = "Bankerohan" },
                    new RoutePoint { Latitude = 7.0680, Longitude = 125.6020, Name = "Downtown Davao" },
                    new RoutePoint { Latitude = 7.0720, Longitude = 125.6080, Name = "Quirino Avenue" },
                    new RoutePoint { Latitude = 7.0760, Longitude = 125.6140, Name = "Agdao District" },
                    new RoutePoint { Latitude = 7.0800, Longitude = 125.6200, Name = "Agdao Terminal" }
                }
            },
            new JeepneyRoute
            {
                Id = "toril-downtown",
                Name = "Toril-Downtown",
                Description = "Toril to Downtown via McArthur Highway",
                Fare = 20.0,
                VehicleType = "Jeepney", 
                EstimatedDuration = TimeSpan.FromMinutes(60),
                Points = new List<RoutePoint>
                {
                    new RoutePoint { Latitude = 7.0200, Longitude = 125.5500, Name = "Toril Terminal" },
                    new RoutePoint { Latitude = 7.0250, Longitude = 125.5550, Name = "Toril Proper" },
                    new RoutePoint { Latitude = 7.0300, Longitude = 125.5600, Name = "Mintal" },
                    new RoutePoint { Latitude = 7.0350, Longitude = 125.5650, Name = "Tugbok District" },
                    new RoutePoint { Latitude = 7.0400, Longitude = 125.5700, Name = "Catalunan Grande" },
                    new RoutePoint { Latitude = 7.0450, Longitude = 125.5750, Name = "Catalunan Pequeño" },
                    new RoutePoint { Latitude = 7.0500, Longitude = 125.5800, Name = "Tibungco" },
                    new RoutePoint { Latitude = 7.0550, Longitude = 125.5850, Name = "Matina Pangi" },
                    new RoutePoint { Latitude = 7.0600, Longitude = 125.5900, Name = "Matina Crossing" },
                    new RoutePoint { Latitude = 7.0644, Longitude = 125.5975, Name = "Bankerohan" },
                    new RoutePoint { Latitude = 7.0680, Longitude = 125.6000, Name = "Downtown Davao" }
                }
            },
            new JeepneyRoute
            {
                Id = "buhangin-downtown",
                Name = "Buhangin-Downtown",
                Description = "Buhangin to Downtown via Panacan",
                Fare = 15.0,
                VehicleType = "Jeepney",
                EstimatedDuration = TimeSpan.FromMinutes(40),
                Points = new List<RoutePoint>
                {
                    new RoutePoint { Latitude = 7.0960, Longitude = 125.6440, Name = "Buhangin Terminal" },
                    new RoutePoint { Latitude = 7.0920, Longitude = 125.6380, Name = "Buhangin Proper" },
                    new RoutePoint { Latitude = 7.0880, Longitude = 125.6320, Name = "Panacan" },
                    new RoutePoint { Latitude = 7.0840, Longitude = 125.6260, Name = "Ecoland" },
                    new RoutePoint { Latitude = 7.0800, Longitude = 125.6200, Name = "Matina Aplaya" },
                    new RoutePoint { Latitude = 7.0760, Longitude = 125.6140, Name = "Matina Crossing" },
                    new RoutePoint { Latitude = 7.0720, Longitude = 125.6080, Name = "Davao Doctors" },
                    new RoutePoint { Latitude = 7.0680, Longitude = 125.6020, Name = "Roxas Avenue" },
                    new RoutePoint { Latitude = 7.0644, Longitude = 125.5975, Name = "Bankerohan" },
                    new RoutePoint { Latitude = 7.0680, Longitude = 125.6000, Name = "Downtown Davao" }
                }
            },
            // Additional comprehensive routes for better coverage
            new JeepneyRoute
            {
                Id = "comprehensive-north",
                Name = "North Davao Comprehensive",
                Description = "Comprehensive northern route covering Buhangin, Panacan, and surrounding areas",
                Fare = 16.0,
                VehicleType = "Jeepney",
                EstimatedDuration = TimeSpan.FromMinutes(55),
                Points = new List<RoutePoint>
                {
                    new RoutePoint { Latitude = 7.0500, Longitude = 125.5800, Name = "South Starting Point" },
                    new RoutePoint { Latitude = 7.0600, Longitude = 125.5900, Name = "Matina Area" },
                    new RoutePoint { Latitude = 7.0700, Longitude = 125.6000, Name = "Central Davao" },
                    new RoutePoint { Latitude = 7.0800, Longitude = 125.6100, Name = "Agdao Area" },
                    new RoutePoint { Latitude = 7.0900, Longitude = 125.6200, Name = "Ecoland Area" },
                    new RoutePoint { Latitude = 7.1000, Longitude = 125.6300, Name = "Buhangin Area" },
                    new RoutePoint { Latitude = 7.1100, Longitude = 125.6400, Name = "Panacan Area" },
                    new RoutePoint { Latitude = 7.1200, Longitude = 125.6500, Name = "North Extension" }
                }
            },
            new JeepneyRoute
            {
                Id = "comprehensive-east-west",
                Name = "East-West Comprehensive",
                Description = "Comprehensive east-west route covering wide area",
                Fare = 17.0,
                VehicleType = "Jeepney",
                EstimatedDuration = TimeSpan.FromMinutes(50),
                Points = new List<RoutePoint>
                {
                    new RoutePoint { Latitude = 7.0700, Longitude = 125.5500, Name = "West Starting Point" },
                    new RoutePoint { Latitude = 7.0720, Longitude = 125.5600, Name = "West Area 1" },
                    new RoutePoint { Latitude = 7.0740, Longitude = 125.5700, Name = "West Area 2" },
                    new RoutePoint { Latitude = 7.0760, Longitude = 125.5800, Name = "Central West" },
                    new RoutePoint { Latitude = 7.0780, Longitude = 125.5900, Name = "Central" },
                    new RoutePoint { Latitude = 7.0800, Longitude = 125.6000, Name = "Central East" },
                    new RoutePoint { Latitude = 7.0820, Longitude = 125.6100, Name = "East Area 1" },
                    new RoutePoint { Latitude = 7.0840, Longitude = 125.6200, Name = "East Area 2" },
                    new RoutePoint { Latitude = 7.0860, Longitude = 125.6300, Name = "East Area 3" },
                    new RoutePoint { Latitude = 7.0880, Longitude = 125.6400, Name = "Far East" }
                }
            }
        };
    }
}
