using System.Windows;
using System.Windows.Controls;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Controls;

public class SettingDataTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? FilePathTemplate { get; set; }
    public DataTemplate? ComboBoxTemplate { get; set; }
    public DataTemplate? FieldsTemplate { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is SettingViewModel setting)
        {
            return setting.Kind switch
            {
                SettingKind.Text => TextTemplate,
                SettingKind.FilePath => FilePathTemplate,
                SettingKind.ComboBox => ComboBoxTemplate,
                SettingKind.Fields => FieldsTemplate,
                _ => TextTemplate
            } ?? base.SelectTemplate(item, container);
        }
        return base.SelectTemplate(item, container);
    }
}
