using JRoute.Models;
using Microsoft.Maui.Maps;
using Supabase;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            var points = new List<RoutePoint>();
            if (string.IsNullOrWhiteSpace(geoJsonContent)) return points;

            var root = JToken.Parse(geoJsonContent);
            var features = root["features"] as JArray;
            if (features == null) return points;

            foreach (var feature in features)
            {
                var geometry = feature?["geometry"];
                if (geometry == null) continue;

                var type = geometry["type"]?.Value<string>();
                if (!string.Equals(type, "LineString", StringComparison.OrdinalIgnoreCase)) continue;

                var coordinates = geometry["coordinates"] as JArray;
                if (coordinates == null) continue;

                for (int i = 0; i < coordinates.Count; i++)
                {
                    var coord = coordinates[i] as JArray;
                    if (coord == null || coord.Count < 2) continue;

                    var lonToken = coord[0];
                    var latToken = coord[1];
                    if (lonToken == null || latToken == null) continue;

                    if (!double.TryParse(lonToken.ToString(), out var lon)) continue;
                    if (!double.TryParse(latToken.ToString(), out var lat)) continue;

                    points.Add(new RoutePoint
                    {
                        Longitude = lon,
                        Latitude = lat,
                        Name = $"Point {i + 1}"
                    });
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

    public async Task<ComputedRoute> FindOptimalRouteAsync(Location currentLocation, Location destination)
    {
        System.Diagnostics.Debug.WriteLine("=== SIMPLIFIED ROUTE ALGORITHM ===");
        System.Diagnostics.Debug.WriteLine($"From: {currentLocation.Latitude:F6},{currentLocation.Longitude:F6} (Current Location)");
        System.Diagnostics.Debug.WriteLine($"To: {destination.Latitude:F6},{destination.Longitude:F6} (Destination)");
        
        // Step 1: Load all available routes from database
        System.Diagnostics.Debug.WriteLine("Step 1: Loading all available routes from database...");
        var allRoutes = await GetAllRoutesAsync();
        System.Diagnostics.Debug.WriteLine($"Loaded {allRoutes.Count} total routes");
        
        // Step 2: Find routes that can connect current location to destination
        System.Diagnostics.Debug.WriteLine("Step 2: Finding routes that connect current location to destination...");
        var viableRoutes = FindViableRoutesToDestination(currentLocation, destination, allRoutes);
        
        if (!viableRoutes.Any())
        {
            System.Diagnostics.Debug.WriteLine("No viable routes found - creating direct walking route");
            return await CreateDirectWalkingRoute(currentLocation, destination);
        }
        
        // Step 3: Select the best route combination (shortest total distance)
        System.Diagnostics.Debug.WriteLine("Step 3: Selecting best route combination...");
        var bestCombination = SelectBestRouteCombination(currentLocation, destination, viableRoutes);
        
        // Step 4: Fill gaps with Google Directions API
        System.Diagnostics.Debug.WriteLine("Step 4: Filling gaps with Google Directions...");
        var finalRoute = await FillGapsWithGoogleDirections(currentLocation, destination, bestCombination);
        
        System.Diagnostics.Debug.WriteLine($"=== FINAL RESULT: {finalRoute.Segments.Count} segments, {finalRoute.TransferCount} transfers ===");
        return finalRoute;
    }

	public async Task<List<ComputedRoute>> FindTopRoutesAsync(Location currentLocation, Location destination, int topN = 3)
	{
		System.Diagnostics.Debug.WriteLine("=== TOP ROUTES (Hitbox) ===");
		System.Diagnostics.Debug.WriteLine($"From: {currentLocation.Latitude:F6},{currentLocation.Longitude:F6}");
		System.Diagnostics.Debug.WriteLine($"To: {destination.Latitude:F6},{destination.Longitude:F6}");

		// Load all routes once
		var allRoutes = await GetAllRoutesAsync();
		if (!allRoutes.Any())
		{
			var direct = await CreateDirectWalkingRoute(currentLocation, destination);
			return new List<ComputedRoute> { direct };
		}

		// Guardrail: if a single route is very near both A and B, prefer it immediately
		var nearRadius = 120.0;
		var nearStartRoutes = FindRoutesWithinRadius(currentLocation, allRoutes, nearRadius);
		var nearEndRoutes = FindRoutesWithinRadius(destination, allRoutes, nearRadius);
		var singleCandidates = nearStartRoutes.Where(rs => nearEndRoutes.Any(re => re.Id == rs.Id)).ToList();
		if (singleCandidates.Any())
		{
			foreach (var route in singleCandidates)
			{
				FindNearestPoint(currentLocation, route.Points, out int pIdx, out double _p);
				FindNearestPoint(destination, route.Points, out int dIdx, out double _d);
				if (pIdx < dIdx)
				{
					var single = new List<RouteMatch>
					{
						new RouteMatch
						{
							Route = route,
							NearestPickupPoint = route.Points[pIdx],
							NearestDropPoint = route.Points[dIdx],
							PickupDistance = CalculateDistance(currentLocation, route.Points[pIdx].Location),
							DropDistance = CalculateDistance(route.Points[dIdx].Location, destination),
							PickupIndex = pIdx,
							DropIndex = dIdx,
							RouteOverlap = 1.0
						}
					};
					var stitched = await FillGapsWithGoogleDirections(currentLocation, destination, single);
					return new List<ComputedRoute> { stitched };
				}
			}
		}

		// Hitbox-first: expand radius until both A and B are near at least one route; then find a chain via route intersections
		var hitboxCombos = FindChainsByExpandingHitbox(currentLocation, destination, allRoutes, Math.Max(1, topN));
		if (hitboxCombos.Any())
		{
			var stitchedHitbox = new List<ComputedRoute>();
			foreach (var combo in hitboxCombos)
			{
				var routed = await FillGapsWithGoogleDirections(currentLocation, destination, combo);
				stitchedHitbox.Add(routed);
			}
			return stitchedHitbox;
		}

		// If no chains found at any radius, fall back to direct walking only
		var fallback = await CreateDirectWalkingRoute(currentLocation, destination);
		return new List<ComputedRoute> { fallback };
	}

	private List<List<RouteMatch>> FindChainsByExpandingHitbox(Location start, Location end, List<JeepneyRoute> routes, int topN)
	{
		// Expand hitbox up to 5km maximum
		var radii = new[] { 100, 250, 500, 800, 1200, 1600, 2000, 2500, 3000, 4000, 5000 };
		foreach (var radius in radii)
		{
			var nearStart = FindRoutesWithinRadius(start, routes, radius);
			var nearEnd = FindRoutesWithinRadius(end, routes, radius);
			System.Diagnostics.Debug.WriteLine($"Hitbox radius {radius}m → nearStart:{nearStart.Count}, nearEnd:{nearEnd.Count}");
			if (!nearStart.Any() || !nearEnd.Any()) continue;

			var chains = FindRouteChains(nearStart, nearEnd, routes);
			if (chains.Any())
			{
				// Convert chains to route matches
				var combos = new List<List<RouteMatch>>();
				foreach (var chain in chains)
				{
					var combo = BuildMatchesForChain(start, end, chain);
					if (combo.Any()) combos.Add(combo);
				}
				return combos
					.OrderBy(c => CalculateTotalCombinationDistance(start, end, c))
					.Take(topN)
					.ToList();
			}
		}
		return new List<List<RouteMatch>>();
	}

	private List<JeepneyRoute> FindRoutesWithinRadius(Location point, List<JeepneyRoute> routes, double radiusMeters)
	{
		var result = new List<JeepneyRoute>();
		foreach (var r in routes)
		{
			if (!r.Points.Any()) continue;
			var d = CalculatePointToPolylineDistanceMeters(point, r.Points);
			if (d <= radiusMeters)
			{
				result.Add(r);
			}
		}
		return result;
	}

	private List<List<JeepneyRoute>> FindRouteChains(List<JeepneyRoute> startRoutes, List<JeepneyRoute> endRoutes, List<JeepneyRoute> allRoutes)
	{
		// Build route graph: edge if any points between two routes are within transfer threshold
		const double transferRadius = 700; // meters
		var routeIndex = allRoutes.ToDictionary(r => r.Id, r => r);
		var adj = new Dictionary<string, List<string>>();
		foreach (var a in allRoutes)
		{
			for (int j = 0; j < allRoutes.Count; j++)
			{
				var b = allRoutes[j];
				if (a.Id == b.Id) continue;
				if (!adj.ContainsKey(a.Id)) adj[a.Id] = new();
				if (RoutesAreNear(a, b, transferRadius))
				{
					adj[a.Id].Add(b.Id);
				}
			}
		}

		// BFS from each start route to any end route, collect a few shortest chains
		var endSet = new HashSet<string>(endRoutes.Select(r => r.Id));
		var chains = new List<List<JeepneyRoute>>();
		foreach (var s in startRoutes)
		{
			var queue = new Queue<List<string>>();
			queue.Enqueue(new List<string> { s.Id });
			var visited = new HashSet<string> { s.Id };

			while (queue.Count > 0 && chains.Count < 6)
			{
				var path = queue.Dequeue();
				var last = path.Last();
				if (endSet.Contains(last))
				{
					chains.Add(path.Select(id => routeIndex[id]).ToList());
					continue;
				}
				if (!adj.TryGetValue(last, out var nexts)) continue;
				foreach (var nx in nexts)
				{
					if (visited.Contains(nx)) continue;
					if (path.Count >= 4) continue; // limit chain length to 4 routes
					visited.Add(nx);
					var newPath = new List<string>(path) { nx };
					queue.Enqueue(newPath);
				}
			}
		}

		// Prefer shortest chains
		return chains
			.OrderBy(c => c.Count)
			.Take(6)
			.ToList();
	}

	private bool RoutesAreNear(JeepneyRoute a, JeepneyRoute b, double thresholdMeters)
	{
		if (!a.Points.Any() || !b.Points.Any()) return false;
		var d1 = CalculatePolylineToPolylineDistanceMeters(a.Points, b.Points);
		if (d1 <= thresholdMeters) return true;
		return false;
	}

	private double CalculatePointToPolylineDistanceMeters(Location point, List<RoutePoint> polyline)
	{
		if (polyline.Count == 1)
		{
			return CalculateDistance(point, polyline[0].Location);
		}
		double minD = double.MaxValue;
		for (int i = 0; i < polyline.Count - 1; i++)
		{
			var a = polyline[i].Location;
			var b = polyline[i + 1].Location;
			var d = CalculatePointToSegmentDistanceMeters(point, a, b);
			if (d < minD) minD = d;
		}
		return minD;
	}

	private double CalculatePolylineToPolylineDistanceMeters(List<RoutePoint> a, List<RoutePoint> b)
	{
		// Sample both polylines and compute min point-to-segment distances
		double minD = double.MaxValue;
		for (int i = 0; i < a.Count - 1; i++)
		{
			var segA1 = a[i].Location;
			var segA2 = a[i + 1].Location;
			for (int j = 0; j < b.Count - 1; j++)
			{
				var d1 = CalculatePointToSegmentDistanceMeters(a[i].Location, b[j].Location, b[j + 1].Location);
				if (d1 < minD) minD = d1;
				var d2 = CalculatePointToSegmentDistanceMeters(b[j].Location, segA1, segA2);
				if (d2 < minD) minD = d2;
			}
		}
		return minD;
	}

	private double CalculatePointToSegmentDistanceMeters(Location p, Location a, Location b)
	{
		// Convert to a local flat coordinate system for small distances
		double latRad = (a.Latitude + b.Latitude) * 0.5 * Math.PI / 180.0;
		double metersPerDegLat = 111320.0;
		double metersPerDegLon = 111320.0 * Math.Cos(latRad);

		(double x, double y) ToXY(Location loc) => (
			(loc.Longitude - a.Longitude) * metersPerDegLon,
			(loc.Latitude - a.Latitude) * metersPerDegLat
		);

		var ap = ToXY(p);
		var aa = (x: 0.0, y: 0.0);
		var bb = ToXY(b);

		var abx = bb.x - aa.x;
		var aby = bb.y - aa.y;
		var apx = ap.x - aa.x;
		var apy = ap.y - aa.y;

		var ab2 = abx * abx + aby * aby;
		if (ab2 <= double.Epsilon)
		{
			var dx = apx;
			var dy = apy;
			return Math.Sqrt(dx * dx + dy * dy);
		}

		var t = (apx * abx + apy * aby) / ab2;
		t = Math.Max(0, Math.Min(1, t));
		var projx = aa.x + t * abx;
		var projy = aa.y + t * aby;
		var dxp = apx - projx;
		var dyp = apy - projy;
		return Math.Sqrt(dxp * dxp + dyp * dyp);
	}

	private List<RouteMatch> BuildMatchesForChain(Location start, Location end, List<JeepneyRoute> chain)
	{
		var matches = new List<RouteMatch>();
		if (!chain.Any()) return matches;

		// First route: pickup nearest to start, drop nearest to transfer to next route (or nearest to end if single)
		var first = chain[0];
		var pickupFirst = FindNearestPoint(start, first.Points, out int pIdx, out double walkToP);
		int currentDropIdx = pIdx;
		for (int r = 0; r < chain.Count; r++)
		{
			var currentRoute = chain[r];
			int startIdx;
			double walkGap;
			if (r == 0)
			{
				startIdx = pIdx;
				walkGap = walkToP;
			}
			else
			{
				FindNearestPoint(matches.Last().NearestDropPoint.Location, currentRoute.Points, out startIdx, out walkGap);
			}

			int endIdx;
			if (r == chain.Count - 1)
			{
				FindNearestPoint(end, currentRoute.Points, out endIdx, out double _d);
				if (endIdx <= startIdx) endIdx = Math.Min(currentRoute.Points.Count - 1, startIdx + 1);
			}
			else
			{
				// Drop where next route is closest
				var nextRoute = chain[r + 1];
				double best = double.MaxValue; endIdx = -1;
				var step = Math.Max(1, currentRoute.Points.Count / 50);
				for (int i = startIdx + 1; i < currentRoute.Points.Count; i += step)
				{
					var d = nextRoute.Points.Min(p => CalculateDistance(currentRoute.Points[i].Location, p.Location));
					if (d < best)
					{
						best = d;
						endIdx = i;
					}
				}
				if (endIdx <= startIdx) endIdx = Math.Min(currentRoute.Points.Count - 1, startIdx + 1);
			}

			matches.Add(new RouteMatch
			{
				Route = currentRoute,
				NearestPickupPoint = currentRoute.Points[startIdx],
				NearestDropPoint = currentRoute.Points[endIdx],
				PickupDistance = (r == 0) ? walkToP : CalculateDistance(matches.Last().NearestDropPoint.Location, currentRoute.Points[startIdx].Location),
				DropDistance = (r == chain.Count - 1) ? CalculateDistance(currentRoute.Points[endIdx].Location, end) : 0,
				PickupIndex = startIdx,
				DropIndex = endIdx,
				RouteOverlap = 1.0
			});
		}

		return matches;
	}

	private List<RouteMatch> BuildGuaranteedSingle(Location start, Location end, List<JeepneyRoute> routes)
	{
		if (!routes.Any()) return new List<RouteMatch>();

		// Nearest route to the user
		var nearest = routes
			.Select(r => new { Route = r, Pickup = FindNearestPoint(start, r.Points, out int idx, out double dist), PickupIdx = idx, WalkToPickup = dist })
			.OrderBy(x => x.WalkToPickup)
			.First();

		var route = nearest.Route;
		if (!route.Points.Any()) return new List<RouteMatch>();

		// Choose drop as the index after pickup that is closest to destination; if none, last point
		int dropIdx = -1;
		double bestDist = double.MaxValue;
		for (int i = Math.Min(nearest.PickupIdx + 1, route.Points.Count - 1); i < route.Points.Count; i++)
		{
			double d = CalculateDistance(route.Points[i].Location, end);
			if (d < bestDist)
			{
				bestDist = d;
				dropIdx = i;
			}
		}

		if (dropIdx <= nearest.PickupIdx)
		{
			// If route is tiny or data odd, ride to the last point
			dropIdx = route.Points.Count - 1;
			bestDist = CalculateDistance(route.Points[dropIdx].Location, end);
			if (dropIdx <= nearest.PickupIdx) return new List<RouteMatch>();
		}

		return new List<RouteMatch>
		{
			new RouteMatch
			{
				Route = route,
				NearestPickupPoint = route.Points[nearest.PickupIdx],
				NearestDropPoint = route.Points[dropIdx],
				PickupDistance = nearest.WalkToPickup,
				DropDistance = bestDist,
				PickupIndex = nearest.PickupIdx,
				DropIndex = dropIdx,
				RouteOverlap = 1.0
			}
		};
	}

	private List<List<RouteMatch>> FindCombinationsFromNearestRoute(Location start, Location end, List<JeepneyRoute> routes, int topN)
	{
		if (!routes.Any()) return new List<List<RouteMatch>>();

		// 1) Find nearest route to the user
		var nearest = routes
			.Select(r => new { Route = r, Pickup = FindNearestPoint(start, r.Points, out int idx, out double dist), PickupIdx = idx, WalkToPickup = dist })
			.OrderBy(x => x.WalkToPickup)
			.First();

		var candidates = new List<(List<RouteMatch> Combo, double Score)>();

		// Single-route candidate from nearest route
		var (dropIdx0, dropPoint0, dropToDest0) = FindBestDropIndexTowardsDestination(nearest.Route, nearest.PickupIdx, end);
		if (dropIdx0 > nearest.PickupIdx)
		{
			var single = new List<RouteMatch>
			{
				new RouteMatch
				{
					Route = nearest.Route,
					NearestPickupPoint = nearest.Route.Points[nearest.PickupIdx],
					NearestDropPoint = nearest.Route.Points[dropIdx0],
					PickupDistance = nearest.WalkToPickup,
					DropDistance = CalculateDistance(nearest.Route.Points[dropIdx0].Location, end),
					PickupIndex = nearest.PickupIdx,
					DropIndex = dropIdx0,
					RouteOverlap = 1.0
				}
			};
			var score = CalculateTotalCombinationDistance(start, end, single);
			candidates.Add((single, score));
		}

		// Two-route candidates: splice nearest route with every other route
		foreach (var r2 in routes)
		{
			if (r2.Id == nearest.Route.Id || !r2.Points.Any()) continue;

			// For r2, choose best drop towards destination
			var (_, r2BestDropPoint, _) = FindBestDropIndexTowardsDestination(r2, 0, end); // we'll refine index later

			// Explore transfer points: iterate samples on nearest route after pickup and r2 before its best drop
			var stepA = Math.Max(1, (nearest.Route.Points.Count - nearest.PickupIdx) / 30);
			var stepB = Math.Max(1, r2.Points.Count / 30);

			(int aBest, int bBest, double bridge) = (-1, -1, double.MaxValue);
			for (int a = nearest.PickupIdx + 1; a < nearest.Route.Points.Count; a += stepA)
			{
				// For r2: allow any pickup index; but we will ensure order with its drop later
				for (int b = 0; b < r2.Points.Count; b += stepB)
				{
					double gap = CalculateDistance(nearest.Route.Points[a].Location, r2.Points[b].Location);
					if (gap < bridge)
					{
						bridge = gap;
						aBest = a;
						bBest = b;
					}
				}
			}

			if (aBest < 0 || bBest < 0) continue;

			// For r2, find best drop after bBest towards destination
			var (r2DropIdx, r2DropPoint, _) = FindBestDropIndexTowardsDestination(r2, bBest, end);
			if (r2DropIdx <= bBest) continue;

			var combo = new List<RouteMatch>
			{
				new RouteMatch
				{
					Route = nearest.Route,
					NearestPickupPoint = nearest.Route.Points[nearest.PickupIdx],
					NearestDropPoint = nearest.Route.Points[aBest],
					PickupDistance = nearest.WalkToPickup,
					DropDistance = bridge, // transfer gap; will be replaced by directions polyline later
					PickupIndex = nearest.PickupIdx,
					DropIndex = aBest,
					RouteOverlap = 1.0
				},
				new RouteMatch
				{
					Route = r2,
					NearestPickupPoint = r2.Points[bBest],
					NearestDropPoint = r2.Points[r2DropIdx],
					PickupDistance = bridge,
					DropDistance = CalculateDistance(r2.Points[r2DropIdx].Location, end),
					PickupIndex = bBest,
					DropIndex = r2DropIdx,
					RouteOverlap = 1.0
				}
			};

			var score2 = CalculateTotalCombinationDistance(start, end, combo);
			candidates.Add((combo, score2));
		}

		return candidates
			.OrderBy(c => c.Score)
			.Take(topN)
			.Select(c => c.Combo)
			.ToList();
	}

	private sealed class GraphNode
	{
		public string RouteId { get; init; } = string.Empty;
		public int Index { get; init; }
		public Location Location { get; init; } = new Location();
	}

	private List<List<RouteMatch>> FindTopRouteCombinationsWithGraph(Location start, Location end, List<JeepneyRoute> routes, int k)
	{
		var routeIdToRoute = routes.ToDictionary(r => r.Id, r => r);

		// Start from the nearest route to the user
		var nearestRoute = routes
			.Select(r => new { Route = r, Nearest = FindNearestPoint(start, r.Points, out int idx, out double dist), Idx = idx, Dist = dist })
			.OrderBy(x => x.Dist)
			.FirstOrDefault();
		if (nearestRoute == null) return new List<List<RouteMatch>>();

		var startKey = (nearestRoute.Route.Id, nearestRoute.Idx);
		const double finalWalkRadius = 1200; // meters
		const double transferRadius = 2000; // meters

		// Build adjacency: along-route edges + transfer edges between close points
		var adjacency = new Dictionary<(string routeId, int idx), List<((string, int) to, double cost)>>();

		// Along-route edges
		foreach (var r in routes)
		{
			for (int i = 0; i < r.Points.Count - 1; i++)
			{
				var a = (r.Id, i);
				var b = (r.Id, i + 1);
				var cost = CalculateDistance(r.Points[i].Location, r.Points[i + 1].Location);
				if (!adjacency.ContainsKey(a)) adjacency[a] = new();
				adjacency[a].Add((b, cost));
			}
		}

		// Transfer edges (downsampled for performance)
		for (int i = 0; i < routes.Count; i++)
		{
			for (int j = i + 1; j < routes.Count; j++)
			{
				var r1 = routes[i];
				var r2 = routes[j];
				var step1 = Math.Max(1, r1.Points.Count / 50);
				var step2 = Math.Max(1, r2.Points.Count / 50);
				for (int a = 0; a < r1.Points.Count; a += step1)
				{
					for (int b = 0; b < r2.Points.Count; b += step2)
					{
						var d = CalculateDistance(r1.Points[a].Location, r2.Points[b].Location);
						if (d <= transferRadius)
						{
							var key1 = (r1.Id, a);
							var key2 = (r2.Id, b);
							if (!adjacency.ContainsKey(key1)) adjacency[key1] = new();
							if (!adjacency.ContainsKey(key2)) adjacency[key2] = new();
							adjacency[key1].Add((key2, d));
							adjacency[key2].Add((key1, d));
						}
					}
				}
			}
		}

		var results = new List<List<RouteMatch>>();
		var penalties = new Dictionary<(string, int, string, int), double>();

		for (int attempt = 0; attempt < k; attempt++)
		{
			var prev = RunDijkstraOnce(adjacency, routeIdToRoute, startKey, end, finalWalkRadius, penalties);
			if (!prev.Any()) break;
			var nodePath = ReconstructPath(prev);
			var combo = ConvertNodePathToRouteMatches(nodePath, routeIdToRoute);
			if (combo.Any())
			{
				results.Add(combo);
				// Penalize used edges to diversify
				for (int t = 0; t < nodePath.Count - 1; t++)
				{
					var e = (nodePath[t].RouteId, nodePath[t].Index, nodePath[t + 1].RouteId, nodePath[t + 1].Index);
					penalties[e] = penalties.GetValueOrDefault(e, 0) + 500; // 500m extra
				}
			}
			else
			{
				break;
			}
		}

		return results;
	}

	private Dictionary<(string, int), (string, int)> RunDijkstraOnce(
		Dictionary<(string, int), List<((string, int) to, double cost)>> adjacency,
		Dictionary<string, JeepneyRoute> routeMap,
		(string, int) startKey,
		Location dest,
		double finalWalkRadius,
		Dictionary<(string, int, string, int), double> penalties)
	{
		var dist = new Dictionary<(string, int), double>();
		var prev = new Dictionary<(string, int), (string, int)>();
		var visited = new HashSet<(string, int)>();
		var cmp = Comparer<(double d, string r, int i)>.Create((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : (a.r != b.r ? string.CompareOrdinal(a.r, b.r) : a.i.CompareTo(b.i)));
		var pq = new SortedSet<(double d, string r, int i)>(cmp);

		dist[startKey] = 0;
		pq.Add((0, startKey.Item1, startKey.Item2));

		(string, int) bestGoal = default;
		double bestGoalDist = double.MaxValue;

		while (pq.Count > 0)
		{
			var current = pq.Min; pq.Remove(current);
			var key = (current.r, current.i);
			if (visited.Contains(key)) continue;
			visited.Add(key);

			var nodeLoc = routeMap[key.Item1].Points[key.Item2].Location;
			var toDest = CalculateDistance(nodeLoc, dest);
			if (toDest <= finalWalkRadius)
			{
				bestGoal = key;
				bestGoalDist = current.d;
				break;
			}

			if (!adjacency.TryGetValue(key, out var edges)) continue;
			foreach (var edge in edges)
			{
				var pen = penalties.GetValueOrDefault((key.Item1, key.Item2, edge.to.Item1, edge.to.Item2), 0);
				var nd = current.d + edge.cost + pen;
				if (!dist.TryGetValue(edge.to, out var old) || nd < old)
				{
					dist[edge.to] = nd;
					prev[edge.to] = key;
					pq.Add((nd, edge.to.Item1, edge.to.Item2));
				}
			}
		}

		if (bestGoalDist == double.MaxValue) return new Dictionary<(string, int), (string, int)>();

		// Trim prev to goal path only
		var trimmed = new Dictionary<(string, int), (string, int)>();
		var cur = bestGoal;
		while (prev.ContainsKey(cur))
		{
			var p = prev[cur];
			trimmed[cur] = p;
			cur = p;
		}
		return trimmed;
	}

	private List<GraphNode> ReconstructPath(Dictionary<(string, int), (string, int)> prev)
	{
		var path = new List<GraphNode>();
		if (!prev.Any()) return path;
		var last = prev.Keys.First();
		while (prev.ContainsKey(last))
		{
			var p = prev[last];
			path.Add(new GraphNode { RouteId = last.Item1, Index = last.Item2 });
			last = p;
		}
		path.Add(new GraphNode { RouteId = last.Item1, Index = last.Item2 });
		path.Reverse();
		return path;
	}

	private List<RouteMatch> ConvertNodePathToRouteMatches(List<GraphNode> nodePath, Dictionary<string, JeepneyRoute> routeMap)
	{
		var matches = new List<RouteMatch>();
		if (nodePath.Count < 2) return matches;

		string currentRouteId = nodePath[0].RouteId;
		int startIdx = nodePath[0].Index;
		int lastIdx = startIdx;

		for (int i = 1; i < nodePath.Count; i++)
		{
			var n = nodePath[i];
			if (n.RouteId == currentRouteId)
			{
				lastIdx = n.Index;
			}
			else
			{
				if (lastIdx > startIdx)
				{
					var route = routeMap[currentRouteId];
					matches.Add(new RouteMatch
					{
						Route = route,
						NearestPickupPoint = route.Points[startIdx],
						NearestDropPoint = route.Points[lastIdx],
						PickupDistance = 0,
						DropDistance = 0,
						PickupIndex = startIdx,
						DropIndex = lastIdx,
						RouteOverlap = 1.0
					});
				}
				currentRouteId = n.RouteId;
				startIdx = n.Index;
				lastIdx = n.Index;
			}
		}

		if (lastIdx > startIdx)
		{
			var route = routeMap[currentRouteId];
			matches.Add(new RouteMatch
			{
				Route = route,
				NearestPickupPoint = route.Points[startIdx],
				NearestDropPoint = route.Points[lastIdx],
				PickupDistance = 0,
				DropDistance = 0,
				PickupIndex = startIdx,
				DropIndex = lastIdx,
				RouteOverlap = 1.0
			});
		}

		return matches;
	}

	private List<RouteMatch> BuildApproximateCombination(Location start, Location end, List<JeepneyRoute> routes)
	{
		// More permissive chaining: allow farther pickups/transfers and approximate last-mile with directions
		const double pickupRadius = 2500; // meters
		const double transferRadius = 2500; // meters
		const double minProgress = 400; // meters closer to destination per segment
		const int maxSegments = 4;

		var current = start;
		var used = new HashSet<string>();
		var chain = new List<RouteMatch>();

		for (int seg = 0; seg < maxSegments; seg++)
		{
			RouteMatch? best = null;
			double bestDropToDest = double.MaxValue;

			foreach (var route in routes)
			{
				if (used.Contains(route.Id) || !route.Points.Any()) continue;

				// Nearest pickup to current, must be within pickupRadius
				var pickup = FindNearestPoint(current, route.Points, out int pickupIdx, out double pickupDist);
				if (pickupDist > pickupRadius) continue;

				// Choose drop along the route after pickup that gets closest to the destination
				var (dropIdx, dropPoint, dropToDest) = FindBestDropIndexTowardsDestination(route, pickupIdx, end);
				if (dropIdx <= pickupIdx) continue;

				// For transfers after first segment, ensure transfer gap is within transferRadius
				if (chain.Any())
				{
					var prevDrop = chain.Last().NearestDropPoint.Location;
					var transferGap = CalculateDistance(prevDrop, pickup.Location);
					if (transferGap > transferRadius) continue;
				}

				// Ensure this segment makes meaningful progress toward the destination
				var currentToDest = CalculateDistance(current, end);
				if (currentToDest - dropToDest < minProgress) continue;

				if (dropToDest < bestDropToDest)
				{
					bestDropToDest = dropToDest;
					best = new RouteMatch
					{
						Route = route,
						NearestPickupPoint = pickup,
						NearestDropPoint = dropPoint,
						PickupDistance = pickupDist,
						DropDistance = dropToDest, // used only for ranking later
						PickupIndex = pickupIdx,
						DropIndex = dropIdx,
						RouteOverlap = 0.0
					};
				}
			}

			if (best == null) break;

			chain.Add(best);
			used.Add(best.Route.Id);
			current = best.NearestDropPoint.Location;

			// Stop early if very close to destination
			if (CalculateDistance(current, end) <= 600) break;
		}

		return chain;
	}

	private (int dropIndex, RoutePoint dropPoint, double dropToDestination) FindBestDropIndexTowardsDestination(JeepneyRoute route, int pickupIndex, Location destination)
	{
		int bestIdx = -1;
		RoutePoint? bestPoint = null;
		double bestDist = double.MaxValue;

		for (int i = pickupIndex + 1; i < route.Points.Count; i++)
		{
			var rp = route.Points[i];
			var d = CalculateDistance(rp.Location, destination);
			if (d < bestDist)
			{
				bestDist = d;
				bestIdx = i;
				bestPoint = rp;
			}
		}

		return bestIdx >= 0 && bestPoint != null
			? (bestIdx, bestPoint, bestDist)
			: (pickupIndex, route.Points[pickupIndex], CalculateDistance(route.Points[pickupIndex].Location, destination));
	}

	private RouteMatch? BuildRelaxedSingle(Location start, Location end, List<JeepneyRoute> routes)
	{
		// Completely relax walking radii; pick the single route that minimizes overall distance
		RouteMatch? best = null;
		double bestScore = double.MaxValue;
		foreach (var route in routes)
		{
			if (!route.Points.Any()) continue;
			var pickup = FindNearestPoint(start, route.Points, out int pIdx, out double pDist);
			var (dIdx, drop, _) = FindBestDropIndexTowardsDestination(route, pIdx, end);
			if (dIdx <= pIdx) continue;
			var total = pDist + CalculateRouteSegmentDistance(route, pIdx, dIdx) + CalculateDistance(drop.Location, end);
			if (total < bestScore)
			{
				bestScore = total;
				best = new RouteMatch
				{
					Route = route,
					NearestPickupPoint = pickup,
					NearestDropPoint = drop,
					PickupDistance = pDist,
					DropDistance = CalculateDistance(drop.Location, end),
					PickupIndex = pIdx,
					DropIndex = dIdx,
					RouteOverlap = 0.0
				};
			}
		}
		return best;
	}

	private List<RouteMatch> BuildSimpleTwoRouteBridge(Location start, Location end, List<JeepneyRoute> routes)
	{
		// Relaxed two-route bridge: choose route A closest to start and route B closest to end,
		// then connect them at their closest pair of points with correct order.
		var bestCombo = new List<RouteMatch>();
		double bestScore = double.MaxValue;

		for (int i = 0; i < routes.Count; i++)
		{
			var rA = routes[i];
			if (!rA.Points.Any()) continue;
			var pickupA = FindNearestPoint(start, rA.Points, out int pAIdx, out double pADist);

			for (int j = 0; j < routes.Count; j++)
			{
				if (i == j) continue;
				var rB = routes[j];
				if (!rB.Points.Any()) continue;
				var dropB = FindNearestPoint(end, rB.Points, out int dBIdx, out double dBDist);

				// Find closest pair between rA after pickup and rB before drop
				double bestBridge = double.MaxValue;
				int bestAIdx = -1;
				int bestBIdx = -1;
				for (int a = pAIdx + 1; a < rA.Points.Count; a++)
				{
					for (int b = 0; b < dBIdx; b++)
					{
						double gap = CalculateDistance(rA.Points[a].Location, rB.Points[b].Location);
						if (gap < bestBridge)
						{
							bestBridge = gap;
							bestAIdx = a;
							bestBIdx = b;
						}
					}
				}

				if (bestAIdx < 0 || bestBIdx < 0) continue;

				var aDrop = rA.Points[bestAIdx];
				var bPickup = rB.Points[bestBIdx];

				// Total score: walk to A + ride A seg + bridge walk + ride B seg + walk to end
				double score = pADist
					+ CalculateRouteSegmentDistance(rA, pAIdx, bestAIdx)
					+ bestBridge
					+ CalculateRouteSegmentDistance(rB, bestBIdx, dBIdx)
					+ dBDist;

				if (score < bestScore)
				{
					bestScore = score;
					bestCombo = new List<RouteMatch>
					{
						new RouteMatch
						{
							Route = rA,
							NearestPickupPoint = pickupA,
							NearestDropPoint = aDrop,
							PickupDistance = pADist,
							DropDistance = bestBridge,
							PickupIndex = pAIdx,
							DropIndex = bestAIdx,
							RouteOverlap = 0.0
						},
						new RouteMatch
						{
							Route = rB,
							NearestPickupPoint = bPickup,
							NearestDropPoint = dropB,
							PickupDistance = bestBridge,
							DropDistance = dBDist,
							PickupIndex = bestBIdx,
							DropIndex = dBIdx,
							RouteOverlap = 0.0
						}
					};
				}
			}
		}

		return bestCombo;
	}

    private List<List<RouteMatch>> FindViableRoutesToDestination(Location currentLocation, Location destination, List<JeepneyRoute> allRoutes)
    {
        var viableRouteCombinations = new List<List<RouteMatch>>();
        const double maxWalkingDistance = 1500; // 1.5km max walking distance
        
        // 1. Try single routes
        foreach (var route in allRoutes)
        {
            var singleRouteMatch = TryCreateSingleRouteMatch(currentLocation, destination, route, maxWalkingDistance);
            if (singleRouteMatch != null)
            {
                viableRouteCombinations.Add(new List<RouteMatch> { singleRouteMatch });
                System.Diagnostics.Debug.WriteLine($"✓ Single route viable: {route.Name}");
            }
        }
        
        // 2. Try two-route combinations
        for (int i = 0; i < allRoutes.Count; i++)
        {
            for (int j = 0; j < allRoutes.Count; j++)
            {
                if (i == j) continue;
                
                var twoRouteMatch = TryCreateTwoRouteMatch(currentLocation, destination, allRoutes[i], allRoutes[j], maxWalkingDistance);
                if (twoRouteMatch != null && twoRouteMatch.Count == 2)
                {
                    viableRouteCombinations.Add(twoRouteMatch);
                    System.Diagnostics.Debug.WriteLine($"✓ Two route combination viable: {allRoutes[i].Name} → {allRoutes[j].Name}");
                }
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"Found {viableRouteCombinations.Count} viable route combinations");
        return viableRouteCombinations;
    }

    private RouteMatch? TryCreateSingleRouteMatch(Location currentLocation, Location destination, JeepneyRoute route, double maxWalkingDistance)
    {
        // Find nearest pickup point from current location
        var pickupPoint = FindNearestPoint(currentLocation, route.Points, out int pickupIndex, out double walkToPickup);
        
        // Find nearest drop point to destination
        var dropPoint = FindNearestPoint(destination, route.Points, out int dropIndex, out double walkFromDrop);
        
        // Validate: pickup must come before drop, and walking distances must be reasonable
        if (pickupIndex >= dropIndex || walkToPickup > maxWalkingDistance || walkFromDrop > maxWalkingDistance)
        {
            return null;
        }
        
        return new RouteMatch
        {
            Route = route,
            NearestPickupPoint = pickupPoint,
            NearestDropPoint = dropPoint,
            PickupDistance = walkToPickup,
            DropDistance = walkFromDrop,
            PickupIndex = pickupIndex,
            DropIndex = dropIndex
        };
    }

    private List<RouteMatch>? TryCreateTwoRouteMatch(Location currentLocation, Location destination, JeepneyRoute route1, JeepneyRoute route2, double maxWalkingDistance)
    {
        // Find best pickup on route1 from current location
        var route1Pickup = FindNearestPoint(currentLocation, route1.Points, out int r1PickupIdx, out double walkToRoute1);
        if (walkToRoute1 > maxWalkingDistance) return null;
        
        // Find best drop on route2 to destination
        var route2Drop = FindNearestPoint(destination, route2.Points, out int r2DropIdx, out double walkFromRoute2);
        if (walkFromRoute2 > maxWalkingDistance) return null;
        
        // Find best connection point between routes
        double bestTransferDistance = double.MaxValue;
        int bestRoute1DropIdx = -1;
        int bestRoute2PickupIdx = -1;
        RoutePoint? bestRoute1Drop = null;
        RoutePoint? bestRoute2Pickup = null;
        
        // Try all possible transfer points
        for (int i = r1PickupIdx + 1; i < route1.Points.Count; i++)
        {
            for (int j = 0; j < r2DropIdx; j++)
            {
                var transferDistance = CalculateDistance(route1.Points[i].Location, route2.Points[j].Location);
                if (transferDistance < bestTransferDistance && transferDistance <= maxWalkingDistance)
                {
                    bestTransferDistance = transferDistance;
                    bestRoute1DropIdx = i;
                    bestRoute2PickupIdx = j;
                    bestRoute1Drop = route1.Points[i];
                    bestRoute2Pickup = route2.Points[j];
                }
            }
        }
        
        if (bestRoute1Drop == null || bestRoute2Pickup == null) return null;
        
        return new List<RouteMatch>
        {
            new RouteMatch
            {
                Route = route1,
                NearestPickupPoint = route1Pickup,
                NearestDropPoint = bestRoute1Drop,
                PickupDistance = walkToRoute1,
                DropDistance = bestTransferDistance,
                PickupIndex = r1PickupIdx,
                DropIndex = bestRoute1DropIdx
            },
            new RouteMatch
            {
                Route = route2,
                NearestPickupPoint = bestRoute2Pickup,
                NearestDropPoint = route2Drop,
                PickupDistance = bestTransferDistance,
                DropDistance = walkFromRoute2,
                PickupIndex = bestRoute2PickupIdx,
                DropIndex = r2DropIdx
            }
        };
    }

    private List<RouteMatch> SelectBestRouteCombination(Location currentLocation, Location destination, List<List<RouteMatch>> viableRoutes)
    {
        if (!viableRoutes.Any()) return new List<RouteMatch>();
        
        var bestCombination = viableRoutes[0];
        double bestTotalDistance = CalculateTotalCombinationDistance(currentLocation, destination, bestCombination);
        
        foreach (var combination in viableRoutes.Skip(1))
        {
            var totalDistance = CalculateTotalCombinationDistance(currentLocation, destination, combination);
            if (totalDistance < bestTotalDistance)
            {
                bestTotalDistance = totalDistance;
                bestCombination = combination;
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"Selected best combination with total distance: {bestTotalDistance:F0}m");
        if (bestCombination.Count == 1)
        {
            System.Diagnostics.Debug.WriteLine($"  Single route: {bestCombination[0].Route.Name}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"  Route combination: {string.Join(" → ", bestCombination.Select(r => r.Route.Name))}");
        }
        
        return bestCombination;
    }

    private double CalculateTotalCombinationDistance(Location currentLocation, Location destination, List<RouteMatch> combination)
    {
        if (!combination.Any()) return double.MaxValue;
        
        double totalDistance = 0;
        
        // Walk from current location to first route
        totalDistance += combination.First().PickupDistance;
        
        // Route segments
        foreach (var routeMatch in combination)
        {
            totalDistance += CalculateRouteSegmentDistance(routeMatch.Route, routeMatch.PickupIndex, routeMatch.DropIndex);
        }
        
        // Transfers between routes
        for (int i = 0; i < combination.Count - 1; i++)
        {
            var transferDistance = CalculateDistance(
                combination[i].NearestDropPoint.Location,
                combination[i + 1].NearestPickupPoint.Location
            );
            totalDistance += transferDistance;
        }
        
        // Walk from last route to destination
        totalDistance += combination.Last().DropDistance;
        
        return totalDistance;
    }

    private async Task<ComputedRoute> FillGapsWithGoogleDirections(Location currentLocation, Location destination, List<RouteMatch> routeCombination)
    {
        var computedRoute = new ComputedRoute();
        
        if (!routeCombination.Any())
        {
            return await CreateDirectWalkingRoute(currentLocation, destination);
        }
        
        // 1. Walking from current location to first route pickup
        var firstRoute = routeCombination.First();
        System.Diagnostics.Debug.WriteLine($"Getting directions from current location to {firstRoute.Route.Name} pickup");
        var walkToFirst = await _googlePlacesService.GetDirectionsRouteAsync(currentLocation, firstRoute.NearestPickupPoint.Location);
        if (walkToFirst.Any())
        {
            computedRoute.WalkingSegments.AddRange(walkToFirst.Select((loc, index) => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = $"Walk to {firstRoute.Route.Name} {index + 1}"
            }));
        }
        
        // 2. Add route segments and transfers
        for (int i = 0; i < routeCombination.Count; i++)
        {
            var routeMatch = routeCombination[i];
            
            // Add the route segment
            computedRoute.Segments.Add(CreateRouteSegment(routeMatch));
            
            // Add transfer walking (if not the last route)
            if (i < routeCombination.Count - 1)
            {
                var nextRoute = routeCombination[i + 1];
                System.Diagnostics.Debug.WriteLine($"Getting transfer directions from {routeMatch.Route.Name} to {nextRoute.Route.Name}");
                var transferWalk = await _googlePlacesService.GetDirectionsRouteAsync(
                    routeMatch.NearestDropPoint.Location,
                    nextRoute.NearestPickupPoint.Location
                );
                
                if (transferWalk.Any())
                {
                    computedRoute.WalkingSegments.AddRange(transferWalk.Select((loc, index) => new RoutePoint
                    {
                        Latitude = loc.Latitude,
                        Longitude = loc.Longitude,
                        Name = $"Transfer to {nextRoute.Route.Name} {index + 1}"
                    }));
                }
            }
        }
        
        // 3. Walking from last route drop to destination
        var lastRoute = routeCombination.Last();
        System.Diagnostics.Debug.WriteLine($"Getting directions from {lastRoute.Route.Name} drop to destination");
        var walkToDestination = await _googlePlacesService.GetDirectionsRouteAsync(lastRoute.NearestDropPoint.Location, destination);
        if (walkToDestination.Any())
        {
            computedRoute.WalkingSegments.AddRange(walkToDestination.Select((loc, index) => new RoutePoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                Name = $"Walk to destination {index + 1}"
            }));
        }
        
        // Calculate totals
        computedRoute.TotalFare = computedRoute.Segments.Sum(s => s.Fare);
        computedRoute.TotalDuration = TimeSpan.FromMinutes(computedRoute.Segments.Sum(s => s.Duration.TotalMinutes) + 
                                                          (computedRoute.WalkingSegments.Count * 0.75)); // Assume 45 seconds per walking point
        computedRoute.TotalDistance = computedRoute.Segments.Sum(s => s.Distance) + 
                                    CalculateTotalCombinationDistance(currentLocation, destination, routeCombination);
        
        return computedRoute;
    }

    private List<RouteMatch> FindShortestPathCombination(Location start, Location end, List<JeepneyRoute> allRoutes)
    {
        System.Diagnostics.Debug.WriteLine($"Finding shortest path from {start.Latitude:F4},{start.Longitude:F4} to {end.Latitude:F4},{end.Longitude:F4}");
        
        var bestCombination = new List<RouteMatch>();
        double bestTotalDistance = double.MaxValue;
        
        // Try single route solutions first
        var singleRouteSolution = FindBestSingleRoute(start, end, allRoutes);
        if (singleRouteSolution != null)
        {
            var singleRouteDistance = CalculateRouteMatchDistance(start, end, new List<RouteMatch> { singleRouteSolution });
            if (singleRouteDistance < bestTotalDistance)
            {
                bestTotalDistance = singleRouteDistance;
                bestCombination = new List<RouteMatch> { singleRouteSolution };
                System.Diagnostics.Debug.WriteLine($"Found single route solution: {singleRouteSolution.Route.Name} (distance: {singleRouteDistance:F0}m)");
            }
        }
        
        // Try two-route combinations
        var twoRouteSolution = FindBestTwoRouteCombination(start, end, allRoutes);
        if (twoRouteSolution.Any())
        {
            var twoRouteDistance = CalculateRouteMatchDistance(start, end, twoRouteSolution);
            if (twoRouteDistance < bestTotalDistance)
            {
                bestTotalDistance = twoRouteDistance;
                bestCombination = twoRouteSolution;
                System.Diagnostics.Debug.WriteLine($"Found two route solution: {string.Join(" → ", twoRouteSolution.Select(r => r.Route.Name))} (distance: {twoRouteDistance:F0}m)");
            }
        }
        
        // Try three-route combinations (if needed)
        var threeRouteSolution = FindBestThreeRouteCombination(start, end, allRoutes);
        if (threeRouteSolution.Any())
        {
            var threeRouteDistance = CalculateRouteMatchDistance(start, end, threeRouteSolution);
            if (threeRouteDistance < bestTotalDistance)
            {
                bestTotalDistance = threeRouteDistance;
                bestCombination = threeRouteSolution;
                System.Diagnostics.Debug.WriteLine($"Found three route solution: {string.Join(" → ", threeRouteSolution.Select(r => r.Route.Name))} (distance: {threeRouteDistance:F0}m)");
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"Best combination total distance: {bestTotalDistance:F0}m");
        return bestCombination;
    }

    private RouteMatch? FindBestSingleRoute(Location start, Location end, List<JeepneyRoute> routes)
    {
        RouteMatch? bestMatch = null;
        double bestScore = double.MaxValue;
        
        foreach (var route in routes)
        {
            var startPoint = FindNearestPoint(start, route.Points, out int startIndex, out double startDistance);
            var endPoint = FindNearestPoint(end, route.Points, out int endIndex, out double endDistance);
            
            // Only consider if start comes before end on the route
            if (startIndex >= endIndex) continue;
            
            // Calculate total distance: walk to start + route segment + walk from end
            var totalDistance = startDistance + endDistance + CalculateRouteSegmentDistance(route, startIndex, endIndex);
            
            if (totalDistance < bestScore)
            {
                bestScore = totalDistance;
                bestMatch = new RouteMatch
                {
                    Route = route,
                    NearestPickupPoint = startPoint,
                    NearestDropPoint = endPoint,
                    PickupDistance = startDistance,
                    DropDistance = endDistance,
                    PickupIndex = startIndex,
                    DropIndex = endIndex
                };
            }
        }
        
        return bestMatch;
    }

    private List<RouteMatch> FindBestTwoRouteCombination(Location start, Location end, List<JeepneyRoute> routes)
    {
        var bestCombination = new List<RouteMatch>();
        double bestScore = double.MaxValue;
        
        for (int i = 0; i < routes.Count; i++)
        {
            for (int j = 0; j < routes.Count; j++)
            {
                if (i == j) continue; // Same route
                
                var route1 = routes[i];
                var route2 = routes[j];
                
                // Find best connection points
                var combination = FindBestTwoRouteConnection(start, end, route1, route2);
                if (combination.Any())
                {
                    var totalDistance = CalculateRouteMatchDistance(start, end, combination);
                    if (totalDistance < bestScore)
                    {
                        bestScore = totalDistance;
                        bestCombination = combination;
                    }
                }
            }
        }
        
        return bestCombination;
    }

    private List<RouteMatch> FindBestThreeRouteCombination(Location start, Location end, List<JeepneyRoute> routes)
    {
        var bestCombination = new List<RouteMatch>();
        double bestScore = double.MaxValue;
        
        for (int i = 0; i < routes.Count; i++)
        {
            for (int j = 0; j < routes.Count; j++)
            {
                for (int k = 0; k < routes.Count; k++)
                {
                    if (i == j || j == k || i == k) continue; // No duplicate routes
                    
                    var route1 = routes[i];
                    var route2 = routes[j];
                    var route3 = routes[k];
                    
                    // Find best three-route connection
                    var combination = FindBestThreeRouteConnection(start, end, route1, route2, route3);
                    if (combination.Any())
                    {
                        var totalDistance = CalculateRouteMatchDistance(start, end, combination);
                        if (totalDistance < bestScore)
                        {
                            bestScore = totalDistance;
                            bestCombination = combination;
                        }
                    }
                }
            }
        }
        
        return bestCombination;
    }

    private async Task<ComputedRoute> CreateDirectWalkingRoute(Location start, Location end)
    {
        var walkingRoute = await _googlePlacesService.GetDirectionsRouteAsync(start, end);
        if (!walkingRoute.Any())
        {
            walkingRoute = new List<Location> { start, end };
        }
        
        var computedRoute = new ComputedRoute();
        computedRoute.WalkingSegments = walkingRoute.Select((loc, index) => new RoutePoint
        {
            Latitude = loc.Latitude,
            Longitude = loc.Longitude,
            Name = $"Walk Point {index + 1}"
        }).ToList();
        
        computedRoute.TotalDistance = CalculateTotalDistance(walkingRoute);
        return computedRoute;
    }

    private async Task<ComputedRoute> ConnectRouteGapsWithDirections(Location start, Location end, List<RouteMatch> routeCombination)
    {
        var computedRoute = new ComputedRoute();
        
        // Add walking segment from start to first route
        if (routeCombination.Any())
        {
            var firstRoute = routeCombination.First();
            var walkToFirst = await _googlePlacesService.GetDirectionsRouteAsync(start, firstRoute.NearestPickupPoint.Location);
            if (walkToFirst.Any())
            {
                computedRoute.WalkingSegments.AddRange(walkToFirst.Select((loc, index) => new RoutePoint
                {
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude,
                    Name = $"Walk to {firstRoute.Route.Name} {index + 1}"
                }));
            }
            
            // Add route segments
            foreach (var routeMatch in routeCombination)
            {
                computedRoute.Segments.Add(CreateRouteSegment(routeMatch));
            }
            
            // Add walking segments between routes
            for (int i = 0; i < routeCombination.Count - 1; i++)
            {
                var currentDrop = routeCombination[i].NearestDropPoint.Location;
                var nextPickup = routeCombination[i + 1].NearestPickupPoint.Location;
                
                var walkBetween = await _googlePlacesService.GetDirectionsRouteAsync(currentDrop, nextPickup);
                if (walkBetween.Any())
                {
                    computedRoute.WalkingSegments.AddRange(walkBetween.Select((loc, index) => new RoutePoint
                    {
                        Latitude = loc.Latitude,
                        Longitude = loc.Longitude,
                        Name = $"Transfer walk {index + 1}"
                    }));
                }
            }
            
            // Add walking segment from last route to destination
            var lastRoute = routeCombination.Last();
            var walkToEnd = await _googlePlacesService.GetDirectionsRouteAsync(lastRoute.NearestDropPoint.Location, end);
            if (walkToEnd.Any())
            {
                computedRoute.WalkingSegments.AddRange(walkToEnd.Select((loc, index) => new RoutePoint
                {
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude,
                    Name = $"Walk to destination {index + 1}"
                }));
            }
            
            // Calculate totals
            computedRoute.TotalFare = computedRoute.Segments.Sum(s => s.Fare);
            computedRoute.TotalDuration = TimeSpan.FromMinutes(computedRoute.Segments.Sum(s => s.Duration.TotalMinutes));
            computedRoute.TotalDistance = computedRoute.Segments.Sum(s => s.Distance) + 
                                        computedRoute.WalkingSegments.Sum(w => CalculateDistance(w.Location, w.Location));
        }
        
        return computedRoute;
    }

    private double CalculateRouteMatchDistance(Location start, Location end, List<RouteMatch> routeMatches)
    {
        if (!routeMatches.Any()) return double.MaxValue;
        
        double totalDistance = 0;
        
        // Distance from start to first route
        totalDistance += routeMatches.First().PickupDistance;
        
        // Distance along routes
        foreach (var match in routeMatches)
        {
            totalDistance += CalculateRouteSegmentDistance(match.Route, match.PickupIndex, match.DropIndex);
        }
        
        // Distance between routes (transfers)
        for (int i = 0; i < routeMatches.Count - 1; i++)
        {
            var currentDrop = routeMatches[i].NearestDropPoint.Location;
            var nextPickup = routeMatches[i + 1].NearestPickupPoint.Location;
            totalDistance += CalculateDistance(currentDrop, nextPickup);
        }
        
        // Distance from last route to end
        totalDistance += routeMatches.Last().DropDistance;
        
        return totalDistance;
    }

    private double CalculateRouteSegmentDistance(JeepneyRoute route, int startIndex, int endIndex)
    {
        double distance = 0;
        for (int i = startIndex; i < endIndex && i < route.Points.Count - 1; i++)
        {
            distance += CalculateDistance(route.Points[i].Location, route.Points[i + 1].Location);
        }
        return distance;
    }

    private List<RouteMatch> FindBestTwoRouteConnection(Location start, Location end, JeepneyRoute route1, JeepneyRoute route2)
    {
        var bestConnection = new List<RouteMatch>();
        double bestScore = double.MaxValue;
        
        // Try all possible connection points between the two routes
        foreach (var point1 in route1.Points)
        {
            foreach (var point2 in route2.Points)
            {
                // Find optimal boarding/alighting points
                var route1Start = FindNearestPoint(start, route1.Points, out int r1StartIdx, out double r1StartDist);
                var route1End = FindNearestPoint(point2.Location, route1.Points, out int r1EndIdx, out double r1EndDist);
                
                var route2Start = FindNearestPoint(point1.Location, route2.Points, out int r2StartIdx, out double r2StartDist);
                var route2End = FindNearestPoint(end, route2.Points, out int r2EndIdx, out double r2EndDist);
                
                // Validate route directions
                if (r1StartIdx >= r1EndIdx || r2StartIdx >= r2EndIdx) continue;
                
                // Calculate total distance
                var totalDistance = r1StartDist + r1EndDist + r2StartDist + r2EndDist +
                                  CalculateRouteSegmentDistance(route1, r1StartIdx, r1EndIdx) +
                                  CalculateRouteSegmentDistance(route2, r2StartIdx, r2EndIdx) +
                                  CalculateDistance(point1.Location, point2.Location);
                
                if (totalDistance < bestScore)
                {
                    bestScore = totalDistance;
                    bestConnection = new List<RouteMatch>
                    {
                        new RouteMatch
                        {
                            Route = route1,
                            NearestPickupPoint = route1Start,
                            NearestDropPoint = route1End,
                            PickupDistance = r1StartDist,
                            DropDistance = r1EndDist,
                            PickupIndex = r1StartIdx,
                            DropIndex = r1EndIdx
                        },
                        new RouteMatch
                        {
                            Route = route2,
                            NearestPickupPoint = route2Start,
                            NearestDropPoint = route2End,
                            PickupDistance = r2StartDist,
                            DropDistance = r2EndDist,
                            PickupIndex = r2StartIdx,
                            DropIndex = r2EndIdx
                        }
                    };
                }
            }
        }
        
        return bestConnection;
    }

    private List<RouteMatch> FindBestThreeRouteConnection(Location start, Location end, JeepneyRoute route1, JeepneyRoute route2, JeepneyRoute route3)
    {
        // Simplified three-route connection - can be optimized further
        var bestConnection = new List<RouteMatch>();
        double bestScore = double.MaxValue;
        
        // Try connecting route1 → route2 → route3
        var twoRouteConnection = FindBestTwoRouteConnection(start, end, route1, route2);
        if (twoRouteConnection.Count == 2)
        {
            // Try to insert route3 between them or after them
            // This is a simplified version - full implementation would be more complex
            var route3Match = FindBestSingleRoute(twoRouteConnection.Last().NearestDropPoint.Location, end, new List<JeepneyRoute> { route3 });
            if (route3Match != null)
            {
                var totalDistance = CalculateRouteMatchDistance(start, end, twoRouteConnection) + 
                                  CalculateRouteMatchDistance(twoRouteConnection.Last().NearestDropPoint.Location, end, new List<RouteMatch> { route3Match });
                
                if (totalDistance < bestScore)
                {
                    bestScore = totalDistance;
                    bestConnection = new List<RouteMatch>(twoRouteConnection) { route3Match };
                }
            }
        }
        
        return bestConnection;
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
        // Samples removed to avoid non-database routes being suggested.
        return new List<JeepneyRoute>();
    }
}
