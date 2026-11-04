using System.IO;
using System.Windows;
using Microsoft.Win32;
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

    public void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream)
    {
        var owner = Application.Current.MainWindow;
        var viewModel = new ParseStudioViewModel(node, upstream);

        var window = new ParseValueWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        // Save writes the settings to the node inside the view model; the window only closes here.
        viewModel.CloseRequested += (_, saved) =>
        {
            if (saved)
            {
                window.DialogResult = true;
            }
            else
            {
                window.Close();
            }
        };

        window.Loaded += (_, _) =>
        {
            if (viewModel.LoadCommand.CanExecute(null))
            {
                viewModel.LoadCommand.Execute(null);
            }
        };

        window.ShowDialog();
    }

    public void ShowExecute(ExecuteViewModel viewModel)
    {
        var owner = Application.Current.MainWindow;

        var window = new ExecuteWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        // Closing the window while a run is going stops the run.
        window.Closed += (_, _) =>
        {
            if (viewModel.CancelCommand.CanExecute(null))
            {
                viewModel.CancelCommand.Execute(null);
            }
        };

        window.ShowDialog();
    }

    public string? PickCsvFile(string? currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the file to run",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };

        var folder = string.IsNullOrWhiteSpace(currentPath) ? null : Path.GetDirectoryName(currentPath);
        if (folder is not null && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
