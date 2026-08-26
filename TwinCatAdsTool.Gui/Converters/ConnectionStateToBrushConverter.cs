using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TwinCAT;

namespace TwinCatAdsTool.Gui.Converters
{
    /// <summary>
    /// Colour of the status dot in the connection bar. A connection that was lost is not the same
    /// as one the user closed, so it gets the warning colour rather than the neutral one.
    /// </summary>
    public class ConnectionStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value is ConnectionState state
                ? BrushKey(state)
                : "TextFillColorDisabledBrush";

            return Application.Current?.TryFindResource(key) as Brush
                   ?? (object)DependencyProperty.UnsetValue;
        }

        private static string BrushKey(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Connected:
                    return "SystemFillColorSuccessBrush";
                case ConnectionState.Lost:
                    return "SystemFillColorCriticalBrush";
                default:
                    return "TextFillColorDisabledBrush";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
