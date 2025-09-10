using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace JRoute.Converters
{
    public class PercentageConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double width && parameter is string percentStr && double.TryParse(percentStr, out double percent))
            {
                return width * percent;
            }
            return value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}