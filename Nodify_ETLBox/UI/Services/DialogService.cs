using System.IO;
using System.Windows;
using Microsoft.Win32;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Project;
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

    public void ShowGenerateDll(GenerateDllViewModel viewModel)
    {
        var owner = Application.Current.MainWindow;

        var window = new GenerateDllWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        window.ShowDialog();
    }

    public string? PickFolder(string? currentFolder)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the output folder for the DLL", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
        {
            dialog.InitialDirectory = currentFolder;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickProjectToOpen(string? currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = ProjectFileFilter
        };

        SetInitialFolder(dialog, currentPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickProjectToSave(string? currentPath, string projectName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save project",
            AddExtension = true,
            DefaultExt = ProjectSerializer.FileExtension,
            FileName = projectName + ProjectSerializer.FileExtension,
            OverwritePrompt = true,
            Filter = ProjectFileFilter
        };

        SetInitialFolder(dialog, currentPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public UnsavedChangesChoice AskUnsavedChanges(string projectName)
    {
        var owner = Application.Current.MainWindow;
        var viewModel = new UnsavedChangesViewModel(projectName);

        var window = new UnsavedChangesWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        viewModel.CloseRequested += (_, _) => window.Close();
        window.ShowDialog();
        return viewModel.Choice;
    }

    public bool AskYesNo(string title, string question)
    {
        // Owned by the active dialog (for example Intake Studio), else the main window.
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var answer = owner is null
            ? MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    public void ShowIntakeStudio(IntakeStudioViewModel viewModel)
    {
        var owner = Application.Current.MainWindow;

        var window = new IntakeStudioWindow
        {
            Owner = owner,
            DataContext = viewModel,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        // The view model asks about unsaved settings once and cancels a running scan; the window only closes.
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closing += (_, e) => e.Cancel = !viewModel.ConfirmClose();
        window.ShowDialog();
    }

    public string? PickIntakeFile(string? currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the file to scan",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = FileFilters.Intake
        };

        SetInitialFolder(dialog, currentPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowError(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private const string ProjectFileFilter = "ETL projects (*.etlproj)|*.etlproj|All files (*.*)|*.*";

    private static void SetInitialFolder(FileDialog dialog, string? currentPath)
    {
        var folder = string.IsNullOrWhiteSpace(currentPath) ? null : Path.GetDirectoryName(currentPath);
        if (folder is not null && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }
    }

    public string? PickCsvFile(string? currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the file to run",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = FileFilters.Csv
        };

        var folder = string.IsNullOrWhiteSpace(currentPath) ? null : Path.GetDirectoryName(currentPath);
        if (folder is not null && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
