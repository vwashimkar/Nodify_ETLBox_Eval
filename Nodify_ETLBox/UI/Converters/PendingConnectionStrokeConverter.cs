using System.Windows.Data;
using System.Windows.Media;

namespace EtlDesigner.UI.Converters;

/// <summary>
/// Converts PendingConnection.IsValid bool to Brush color (green if valid, red if not).
/// </summary>
public class PendingConnectionStrokeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isValid)
        {
            return isValid
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(Colors.Red);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
