using System;
using System.Globalization;
using System.Windows.Data;

namespace DerivSmartBotDesktop
{
    /// <summary>
    /// Converts IsConnected (bool) to "Connected" / "Disconnected"
    /// </summary>
    public class BoolToTextConnectedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? "Connected" : "Disconnected";
            }
            return "Disconnected";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Not used in this app
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts IsRunning (bool) to "Running" / "Stopped"
    /// </summary>
    public class BoolToTextBotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? "Running" : "Stopped";
            }
            return "Stopped";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Not used in this app
            throw new NotImplementedException();
        }
    }
}
