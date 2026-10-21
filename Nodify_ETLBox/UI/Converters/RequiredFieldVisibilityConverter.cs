using System.Windows;
using System.Windows.Data;

namespace EtlDesigner.UI.Converters;

/// <summary>
/// Shows red asterisk only if Required=true AND Value is empty.
/// </summary>
public class RequiredFieldVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
