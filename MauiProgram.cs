using Microsoft.Maui.Controls.Maps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Storage;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sb = Supabase;

#if ANDROID
using Android.Gms.Maps;
using Microsoft.Maui.Maps.Handlers;
using Microsoft.Maui.Handlers;   // EntryHandler, EditorHandler
using Android.Widget;            // EditText
#endif

namespace JRoute;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiMaps()
            .ConfigureMauiHandlers(handlers =>
            {
#if ANDROID
                handlers.AddHandler<Microsoft.Maui.Controls.Maps.Map, CustomMapHandler>();

                EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
                {
                    if (handler.PlatformView is EditText et) et.Background = null;
                });

                EditorHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
                {
                    if (handler.PlatformView is EditText et) et.Background = null;
                });
#endif
            });

        // ---- Supabase: load config & register client ----
        var cfg = LoadSupabaseConfig();
        builder.Services.AddSingleton<Sb.Client>(_ =>
            new Sb.Client(cfg.Url, cfg.Anon, new Sb.SupabaseOptions
            {
                AutoConnectRealtime = false
            }));
        // --------------------------------------------------

        builder.Services.AddSingleton<IGeoJsonProvider, SupabaseGeoJsonProvider>();

        return builder.Build();
    }

    private static SupabaseConfig LoadSupabaseConfig()
    {
        using var stream = FileSystem.OpenAppPackageFileAsync("supabase.json").GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var json = reader.ReadToEnd();

        var cfg = JsonSerializer.Deserialize<SupabaseConfig>(
                      json,
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                  ) ?? throw new InvalidOperationException("Invalid Resources/Raw/supabase.json");

        if (string.IsNullOrWhiteSpace(cfg.Url) || string.IsNullOrWhiteSpace(cfg.Anon))
            throw new InvalidOperationException("Supabase config missing url/anon.");

        return cfg;
    }

    private sealed class SupabaseConfig
    {
        [JsonPropertyName("url")]  public string Url  { get; set; } = "";
        [JsonPropertyName("anon")] public string Anon { get; set; } = "";
    }
}

#if ANDROID
public class CustomMapHandler : MapHandler
{
    protected override void ConnectHandler(MapView platformView)
    {
        base.ConnectHandler(platformView);

        platformView.GetMapAsync(new MapReadyCallback((googleMap) =>
        {
            googleMap.UiSettings.ZoomControlsEnabled = false;
            googleMap.UiSettings.MyLocationButtonEnabled = false;
        }));
    }
}

public class MapReadyCallback : Java.Lang.Object, IOnMapReadyCallback
{
    private readonly System.Action<GoogleMap> _onMapReady;

    public MapReadyCallback(System.Action<GoogleMap> onMapReady) => _onMapReady = onMapReady;

    public void OnMapReady(GoogleMap googleMap) => _onMapReady?.Invoke(googleMap);
}
#endif
