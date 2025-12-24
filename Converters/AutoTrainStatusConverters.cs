using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DerivSmartBotDesktop.Converters
{
    [ValueConversion(typeof(string), typeof(string))]
    public sealed class AutoTrainStatusToBadgeTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string ?? string.Empty;
            var level = GetLevel(status);
            return level switch
            {
                AutoTrainLevel.Error => "Error",
                AutoTrainLevel.Warning => "Warning",
                _ => "OK"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static AutoTrainLevel GetLevel(string status)
        {
            var text = status.ToLowerInvariant();
            if (text.Contains("failed") || text.Contains("[err]") || text.Contains("missing deps") || text.Contains("not available"))
                return AutoTrainLevel.Error;
            if (text.Contains("starting") || text.Contains("no trade logs") || text.Contains("waiting"))
                return AutoTrainLevel.Warning;
            return AutoTrainLevel.Ok;
        }
    }

    [ValueConversion(typeof(string), typeof(Brush))]
    public sealed class AutoTrainStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string ?? string.Empty;
            var text = status.ToLowerInvariant();
            var kind = text.Contains("failed") || text.Contains("[err]") || text.Contains("missing deps") || text.Contains("not available")
                ? AutoTrainLevel.Error
                : (text.Contains("starting") || text.Contains("no trade logs") || text.Contains("waiting"))
                    ? AutoTrainLevel.Warning
                    : AutoTrainLevel.Ok;

            return kind switch
            {
                AutoTrainLevel.Error => FindBrush("NegativeBrush", Brushes.IndianRed),
                AutoTrainLevel.Warning => FindBrush("WarningBrush", Brushes.Goldenrod),
                _ => FindBrush("PositiveBrush", Brushes.ForestGreen)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static Brush FindBrush(string key, Brush fallback)
        {
            if (Application.Current?.Resources.Contains(key) == true &&
                Application.Current.Resources[key] is Brush brush)
            {
                return brush;
            }

            return fallback;
        }
    }

    internal enum AutoTrainLevel
    {
        Ok,
        Warning,
        Error
    }
}
