using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;
using Nodify_ETLBox.UI;

namespace EtlDesigner.UI.Services;

public class DialogService : IDialogService
{
    public void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings)
    {
        var owner = Application.Current.MainWindow;

        var window = new DataStudioWindow
        {
            Owner = owner,
            DataContext = new DataStudioViewModel(node, settings),
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        window.ShowDialog();
    }
}
