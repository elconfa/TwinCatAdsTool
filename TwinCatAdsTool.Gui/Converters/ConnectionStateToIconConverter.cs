using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;
using TwinCAT;

namespace TwinCatAdsTool.Gui.Converters
{
    public class ConnectionStateToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ConnectionState connectionState)
            {
                switch (connectionState)
                {
                    case ConnectionState.None:
                    case ConnectionState.Lost:
                        return PackIconKind.Minus;
                    case ConnectionState.Disconnected:
                        return PackIconKind.LinkOff;
                    case ConnectionState.Connected:
                        return PackIconKind.Link;
                    default:
                        return DependencyProperty.UnsetValue;
                }
            }

            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
