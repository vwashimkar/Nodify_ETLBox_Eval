using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Converters;

public class NodeStateToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NodeState state)
        {
            return state switch
            {
                NodeState.Idle => new SolidColorBrush(Colors.LightGray),
                NodeState.Running => new SolidColorBrush(Colors.LightBlue),
                NodeState.Succeeded => new SolidColorBrush(Colors.LightGreen),
                NodeState.Failed => new SolidColorBrush(Colors.LightCoral),
                _ => new SolidColorBrush(Colors.White)
            };
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
