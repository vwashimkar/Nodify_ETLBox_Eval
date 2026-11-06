using System.Collections.ObjectModel;
using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Project;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>New, Open, Save, Save As, the recent list and the unsaved-changes question.</summary>
public partial class MainViewModel
{
    public const string UntitledProjectName = "Untitled";

    private const string WindowTitleSuffix = "Data Flow ETL Designer";

    private readonly IRecentProjects _recentProjects;
    private string? _projectPath;
    private string _projectName = UntitledProjectName;
    private bool _isOpeningProject;
    private bool _isRecentOpen;
    private CancellationTokenSource? _refreshCancel;

    public RelayCommand NewProjectCommand { get; private set; } = null!;

    public AsyncRelayCommand OpenProjectCommand { get; private set; } = null!;

    public RelayCommand SaveProjectCommand { get; private set; } = null!;

    public RelayCommand SaveProjectAsCommand { get; private set; } = null!;

    public ObservableCollection<RecentProjectViewModel> RecentProjects { get; } = new();

    public bool HasNoRecentProjects => RecentProjects.Count == 0;

    /// <summary>The Recent drop-down. Its entries are read again each time it opens, so "not found" is current.</summary>
    public bool IsRecentOpen
    {
        get => _isRecentOpen;
        set
        {
            if (value)
            {
                RefreshRecentProjects();
            }

            SetProperty(ref _isRecentOpen, value);
        }
    }

    /// <summary>The open project file, or null while the project has never been saved.</summary>
    public string? ProjectPath => _projectPath;

    public string ProjectName => _projectName;

    public string WindowTitle => $"{_projectName}{(Editor.IsDirty ? "*" : string.Empty)} - {WindowTitleSuffix}";

    /// <summary>The background read of the CsvFile nodes started by the last Open.</summary>
    internal Task ProjectRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Set by a test to make a step of the safe save fail.</summary>
    internal IProjectFileSteps? ProjectFileSteps { get; set; }

    private void CreateProjectCommands()
    {
        bool NotBusy(object? _) => !_isRunInProgress && !_isGenerating;

        NewProjectCommand = new RelayCommand(_ => NewProject(), NotBusy);
        OpenProjectCommand = new AsyncRelayCommand(_ => OpenProjectAsync(), NotBusy);
        SaveProjectCommand = new RelayCommand(_ => SaveProject(), NotBusy);
        SaveProjectAsCommand = new RelayCommand(_ => SaveProjectAs(), NotBusy);

        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.IsDirty))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        };

        RefreshRecentProjects();
    }

    private void RaiseProjectCommandStates()
    {
        NewProjectCommand?.RaiseCanExecuteChanged();
        OpenProjectCommand?.RaiseCanExecuteChanged();
        SaveProjectCommand?.RaiseCanExecuteChanged();
        SaveProjectAsCommand?.RaiseCanExecuteChanged();
    }

    public void NewProject()
    {
        if (!ConfirmUnsavedChanges())
        {
            return;
        }

        CancelProjectRefresh();
        Editor.ClearCanvas();
        ForgetRunResults();
        SetProject(null, UntitledProjectName);
    }

    /// <summary>
    /// Opens a project, from <paramref name="path"/> or a file the user picks. A file that cannot
    /// be read leaves the canvas and the current project as they are. Files are read off the UI
    /// thread, and the CsvFile nodes are refreshed in the background.
    /// </summary>
    public async Task OpenProjectAsync(string? path = null)
    {
        if (!ConfirmUnsavedChanges())
        {
            return;
        }

        path ??= _dialogService.PickProjectToOpen(_projectPath);
        if (path is null)
        {
            return;
        }

        ProjectFile project;
        try
        {
            project = await Task.Run(() => ProjectSerializer.Deserialize(ProjectFileStore.Read(path)));
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Open project", ex is ProjectLoadException
                ? ex.Message
                : $"The project could not be opened: {ex.Message} Check the file and try again.");
            return;
        }

        CancelProjectRefresh();
        _isOpeningProject = true;
        try
        {
            _columnReadsTried.Clear();
            Editor.FromProject(project, path);
            ForgetRunResults();
            SetProject(path, string.IsNullOrWhiteSpace(project.ProjectName) ? Path.GetFileNameWithoutExtension(path) : project.ProjectName);
            AddToRecentProjects(path);

            // Only a legacy chain has a country list; the refresh would add legacy lines to a new graph's Issues.
            if (Editor.Nodes.Any(n => n.NodeType == NodeType.FilterByCountry))
            {
                _ = RefreshCountriesAsync();
            }
        }
        finally
        {
            _isOpeningProject = false;
        }

        _refreshCancel = new CancellationTokenSource();
        ProjectRefresh = RefreshCsvNodesAsync(_refreshCancel.Token);
    }

    /// <summary>Saves to the open file, or asks where when there is none. False when nothing was saved.</summary>
    public bool SaveProject() => _projectPath is null ? SaveProjectAs() : SaveTo(_projectPath, _projectName);

    /// <summary>Asks where to save. The project takes the name of the file. False when nothing was saved.</summary>
    public bool SaveProjectAs()
    {
        var path = _dialogService.PickProjectToSave(_projectPath, _projectName);
        return path is not null && SaveTo(path, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>The close handler: true when the window may close. Never hides an exception.</summary>
    public bool ConfirmClose()
    {
        try
        {
            if (!ConfirmUnsavedChanges())
            {
                return false;
            }

            CancelProjectRefresh();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Window closing", ex);
            _dialogService.ShowError("Close", $"The window stays open so that no work is lost. Save the project with Ctrl+S, then close again. Details: {ex}");
            return false;
        }
    }

    /// <summary>True when the action may go on: nothing unsaved, or the user chose Don't save, or the save worked.</summary>
    private bool ConfirmUnsavedChanges()
    {
        if (!Editor.IsDirty)
        {
            return true;
        }

        return _dialogService.AskUnsavedChanges(_projectName) switch
        {
            UnsavedChangesChoice.Save => SaveProject(),
            UnsavedChangesChoice.DontSave => true,
            _ => false
        };
    }

    private bool SaveTo(string path, string projectName)
    {
        try
        {
            var now = DateTime.UtcNow;
            var savedUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            ProjectFileStore.Save(path, ProjectSerializer.Serialize(Editor.ToProject(projectName, path, savedUtc)), ProjectFileSteps);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Save project", $"The project was not saved, and the old file is unchanged. Reason: {ex.Message} Close any program that uses the file, or save to another folder.");
            return false;
        }

        Editor.MarkClean();
        SetProject(path, projectName);
        AddToRecentProjects(path);
        return true;
    }

    private void SetProject(string? path, string projectName)
    {
        _projectPath = path;
        _projectName = projectName;
        OnPropertyChanged(nameof(ProjectPath));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Results are not part of a project: the next graph starts with none, and Generate DLL waits for a new run.</summary>
    private void ForgetRunResults()
    {
        _lastSuccessfulPipelineRun = null;
        ClearSuccessfulRunSnapshot();
        FilteredJsonOutput = string.Empty;
        ParsedJsonOutput = string.Empty;
        UpdateCommandStates();
    }

    private void CancelProjectRefresh()
    {
        _refreshCancel?.Cancel();
        _refreshCancel = null;
    }

    /// <summary>
    /// Reads the column names of the configured CsvFile nodes, then validates, so that a ParseValue
    /// node gets its columns. A node that cannot be read keeps its normal message.
    /// </summary>
    private async Task RefreshCsvNodesAsync(CancellationToken token)
    {
        var nodes = Editor.Nodes
            .Where(n => n.NodeType == NodeType.CsvFile && n.ConfigurationStatus == "Configured")
            .ToList();

        foreach (var node in nodes)
        {
            node.IsRefreshing = true;
            _columnReadsTried.Add((node.Id, node.GetCsvFileSettings()));
        }

        foreach (var node in nodes)
        {
            try
            {
                await Task.Run(() => ParseValueUpstreamResolver.RefreshColumnsAsync(node, new CsvPreviewReader(), token));
            }
            catch (Exception ex)
            {
                Editor.Issues.Add($"{node.Title}: could not read the columns of the file. Press Load preview on the node. Details: {ex.Message}");
            }
            finally
            {
                node.IsRefreshing = false;
            }
        }

        if (!token.IsCancellationRequested)
        {
            Editor.Validate();
        }
    }

    private void AddToRecentProjects(string path)
    {
        try
        {
            _recentProjects.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Editor.Issues.Add($"Recent projects: the list could not be saved. The project itself is fine. Details: {ex.Message}");
        }

        RefreshRecentProjects();
    }

    private void RemoveFromRecentProjects(string path)
    {
        try
        {
            _recentProjects.Remove(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Editor.Issues.Add($"Recent projects: the list could not be saved. Details: {ex.Message}");
        }

        RefreshRecentProjects();
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var path in _recentProjects.Load())
        {
            RecentProjects.Add(new RecentProjectViewModel(path, File.Exists(path),
                open: () =>
                {
                    IsRecentOpen = false;
                    return OpenProjectAsync(path);
                },
                remove: () => RemoveFromRecentProjects(path)));
        }

        OnPropertyChanged(nameof(HasNoRecentProjects));
    }
}
