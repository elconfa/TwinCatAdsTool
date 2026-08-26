using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TwinCatAdsTool.Gui.Converters
{
    /// <summary>
    /// Bridges <see cref="Wpf.Ui.Controls.NumberBox"/>, whose value is a double, and the strongly
    /// typed NewValue of a symbol view model - short, uint, float and the rest. Wpf's default
    /// converter refuses those conversions, so the value would silently never reach the plc.
    /// </summary>
    public class NumberBoxValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return null;
            }

            try
            {
                return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return DependencyProperty.UnsetValue;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return DependencyProperty.UnsetValue;
            }

            var target = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                // Out of range for the plc type: keep the last good value instead of throwing
                // inside the binding, where the exception would be swallowed anyway.
                return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return DependencyProperty.UnsetValue;
            }
        }
    }
}
