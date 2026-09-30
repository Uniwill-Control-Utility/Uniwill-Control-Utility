using System;
using System.Globalization;
using System.Windows.Data;

namespace Uniwill_Control_Utility.Helpers
{
    /// <summary>
    /// Converts a battery mode index (0, 1, 2) to a display string ("100%", "80%", "60%").
    /// </summary>
    internal sealed class BatteryModeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int mode)
            {
                return mode switch
                {
                    0 => "100%",
                    1 => "80%",
                    2 => "60%",
                    _ => "--%"
                };
            }
            return "--%";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
