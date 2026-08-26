using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace TwinCatAdsTool.Gui.Converters
{
    /// <summary>
    /// Turns a <see cref="SymbolRegular"/> coming from a binding into the
    /// <see cref="IconElement"/> the wpf ui controls expect. Their built in type converter only
    /// runs on literals written in xaml, so a bound icon needs this on the way through.
    /// </summary>
    public class SymbolToIconElementConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SymbolRegular symbol)
            {
                return new SymbolIcon { Symbol = symbol };
            }

            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
