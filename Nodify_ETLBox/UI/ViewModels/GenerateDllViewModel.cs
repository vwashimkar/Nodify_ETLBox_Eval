using System.Collections.ObjectModel;
using System.IO;
using EtlDesigner.Engine.Generation;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>One line of the read-only summary: what will be baked into the DLL.</summary>
public sealed record BakedSetting(string Name, string Value);

/// <summary>
/// The Generate DLL dialog for CsvFile -> ParseValue -> DotNetObject: names, output folder,
/// a summary of the baked settings, Generate (off the UI thread) and the result.
/// </summary>
public sealed class GenerateDllViewModel : ObservableObject
{
    public const string ProjectFolderMessage = OutputFolderCheck.ProjectFolderMessage;

    private readonly PipelineDefinition _definition;
    private readonly IReadOnlyList<string> _outputColumns;
    private readonly Func<CsvPipelineDllRequest, CsvPipelineDllResult> _generate;
    private readonly Func<string?, string?>? _pickFolder;
    private readonly Action<string>? _copyText;
    private readonly Func<string, bool> _isInsideProject;
    private string _assemblyName = CsvPipelineDllGenerator.DefaultClassName;
    private string _namespace = CsvPipelineDllGenerator.DefaultNamespace;
    private string _outputFolder;
    private string? _checkedFolder;
    private string? _checkedFolderError;
    private bool _isGenerating;
    private bool _succeeded;
    private string? _dllPath;
    private string? _readmePath;

    /// <param name="outputColumns">The columns of the tested Execute run.</param>
    /// <param name="generate">Generates the DLL; the real generator by default (tests pass a fake).</param>
    /// <param name="pickFolder">Shows a folder dialog starting at the given folder; null when the user cancels.</param>
    /// <param name="isInsideProject">True when a folder is at or under a .csproj or .sln.</param>
    public GenerateDllViewModel(
        PipelineDefinition definition,
        IReadOnlyList<string> outputColumns,
        Func<CsvPipelineDllRequest, CsvPipelineDllResult>? generate = null,
        Func<string?, string?>? pickFolder = null,
        Action<string>? copyText = null,
        Func<string, bool>? isInsideProject = null,
        string? outputFolder = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _outputColumns = outputColumns ?? throw new ArgumentNullException(nameof(outputColumns));
        _generate = generate ?? CsvPipelineDllGenerator.Generate;
        _pickFolder = pickFolder;
        _copyText = copyText;
        _isInsideProject = isInsideProject ?? OutputFolderCheck.IsInsideProject;
        _outputFolder = outputFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CsvPipeline");
        Summary = BuildSummary(definition);

        GenerateCommand = new AsyncRelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        BrowseCommand = new RelayCommand(_ => Browse(), _ => !IsGenerating && _pickFolder is not null);
        CopyUsageCommand = new RelayCommand(_ => _copyText?.Invoke(UsageExample), _ => _copyText is not null);
    }

    public AsyncRelayCommand GenerateCommand { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand CopyUsageCommand { get; }

    public IReadOnlyList<BakedSetting> Summary { get; }

    public string AssemblyName
    {
        get => _assemblyName;
        set
        {
            if (SetProperty(ref _assemblyName, value ?? string.Empty))
            {
                OnInputChanged(nameof(AssemblyNameError));
            }
        }
    }

    public string Namespace
    {
        get => _namespace;
        set
        {
            if (SetProperty(ref _namespace, value ?? string.Empty))
            {
                OnInputChanged(nameof(NamespaceError));
            }
        }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetProperty(ref _outputFolder, value ?? string.Empty))
            {
                OnInputChanged(nameof(OutputFolderError));
            }
        }
    }

    public string? AssemblyNameError => CsvPipelineDllGenerator.CheckAssemblyName(AssemblyName.Trim());

    public string? NamespaceError => CsvPipelineDllGenerator.CheckNamespace(Namespace.Trim());

    /// <summary>The check writes a small test file, so it runs once per folder text, not on every read.</summary>
    public string? OutputFolderError
    {
        get
        {
            if (_checkedFolder != OutputFolder)
            {
                _checkedFolder = OutputFolder;
                _checkedFolderError = OutputFolderCheck.Check(OutputFolder, _isInsideProject);
            }

            return _checkedFolderError;
        }
    }

    public bool CanGenerate => !IsGenerating && AssemblyNameError is null && NamespaceError is null && OutputFolderError is null;

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                GenerateCommand.RaiseCanExecuteChanged();
                BrowseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool Succeeded
    {
        get => _succeeded;
        private set => SetProperty(ref _succeeded, value);
    }

    public string? DllPath
    {
        get => _dllPath;
        private set => SetProperty(ref _dllPath, value);
    }

    public string? ReadmePath
    {
        get => _readmePath;
        private set => SetProperty(ref _readmePath, value);
    }

    /// <summary>Why the last Generate failed (refusal or compile failure). Empty after a success.</summary>
    public ObservableCollection<string> Messages { get; } = new();

    public ObservableCollection<CompileError> CompileErrors { get; } = new();

    public string UsageExample => CsvPipelineDllGenerator.UsageExample(AssemblyName.Trim(), Namespace.Trim());

    public async Task GenerateAsync()
    {
        if (!CanGenerate)
        {
            return;
        }

        IsGenerating = true;
        Succeeded = false;
        DllPath = null;
        ReadmePath = null;
        Messages.Clear();
        CompileErrors.Clear();

        var request = new CsvPipelineDllRequest(_definition, _outputColumns, OutputFolder.Trim(), AssemblyName.Trim(), Namespace.Trim());
        CsvPipelineDllResult result;
        try
        {
            result = await Task.Run(() => _generate(request));
        }
        catch (Exception ex)
        {
            result = new CsvPipelineDllResult(false, null, null,
                new[] { $"The DLL could not be generated. Check the folder and try again. Details: {ex.Message}" }, Array.Empty<CompileError>());
        }
        finally
        {
            IsGenerating = false;
        }

        Succeeded = result.Success;
        DllPath = result.DllPath;
        ReadmePath = result.ReadmePath;
        foreach (var message in result.Messages)
        {
            Messages.Add(message);
        }

        foreach (var error in result.CompileErrors)
        {
            CompileErrors.Add(error);
        }
    }

    private void OnInputChanged(string errorProperty)
    {
        OnPropertyChanged(errorProperty);
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(UsageExample));
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void Browse()
    {
        var chosen = _pickFolder?.Invoke(OutputFolder);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            OutputFolder = chosen;
        }
    }

    private static IReadOnlyList<BakedSetting> BuildSummary(PipelineDefinition definition)
    {
        var parse = definition.ParseValue;
        var levels = string.Join("  ", parse.DelimiterLevels.Select((l, i) => $"L{i + 1} \"{l.Delimiter}\" {l.Mode}"));
        var slots = string.Join("; ", parse.Slots.Select(s => $"{s.Name}: {string.Join(", ", s.Columns)}"));
        var carry = parse.CarryColumns.AllOtherColumns ? "all other columns"
            : parse.CarryColumns.Names.Count == 0 ? "none"
            : string.Join(", ", parse.CarryColumns.Names);

        return new[]
        {
            new BakedSetting("Source column", parse.SourceColumn),
            new BakedSetting("Levels", levels),
            new BakedSetting("Slots", $"{parse.Slots.Count} ({slots})"),
            new BakedSetting("Carry", carry),
            new BakedSetting("Output type", ObjectKinds.Get(definition.Output.Kind).DisplayName)
        };
    }
}
