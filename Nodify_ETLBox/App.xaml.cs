using System.Windows;
using EtlDesigner.UI.ViewModels;

namespace Nodify_ETLBox;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Manually compose ViewModels
        var mainViewModel = new MainViewModel();

        // Set MainWindow DataContext
        if (MainWindow != null)
        {
            MainWindow.DataContext = mainViewModel;
        }
    }
}
