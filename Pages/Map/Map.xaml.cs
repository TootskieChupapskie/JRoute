using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace JRoute;

public partial class MapPage : ContentPage
{
    public MapPage()
    {
        InitializeComponent();

        // Center map on Davao City (example)
        var center = new Location(7.1907, 125.4553); 
        MyMap.MoveToRegion(MapSpan.FromCenterAndRadius(center, Distance.FromKilometers(5)));
    }
}