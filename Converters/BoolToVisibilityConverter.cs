using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BulkImageGenerator.Converters
{
    /// <summary>
    /// Converts bool → Visibility for showing/hiding panels.
    /// Set ConverterParameter="Inverse" to invert (false → Visible).
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            bool inverse = parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
            if (inverse) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }
}
