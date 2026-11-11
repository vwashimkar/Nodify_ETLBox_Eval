using System.IO;
using System.Text.Json;
using System.Windows;
using EtlDesigner.Engine.Project;

namespace EtlDesigner.UI.ViewModels;

/// <summary>The canvas and the project file: ToProject, FromProject and dirty tracking.</summary>
public partial class EditorViewModel
{
    private static readonly IReadOnlyDictionary<string, NodeType> NodeTypesById = new Dictionary<string, NodeType>
    {
        [ProjectNodeTypes.CsvSource] = NodeType.CsvSource,
        [ProjectNodeTypes.CsvFile] = NodeType.CsvFile,
        [ProjectNodeTypes.FilterByCountry] = NodeType.FilterByCountry,
        [ProjectNodeTypes.ToJson] = NodeType.ToJson,
        [ProjectNodeTypes.ParseJson] = NodeType.ParseJson,
        [ProjectNodeTypes.ResultJson] = NodeType.ResultJson,
        [ProjectNodeTypes.ParseValue] = NodeType.ParseValue,
        [ProjectNodeTypes.DotNetObject] = NodeType.DotNetObject,
        [ProjectNodeTypes.FileIntake] = NodeType.FileIntake,
        [ProjectNodeTypes.CsvPart] = NodeType.CsvPart
    };

    /// <summary>A connection that is not on the canvas but stays in the file. Message null: the unknown node's own line covers it.</summary>
    private sealed record KeptConnection(int Index, ProjectConnection Connection, string? Message);

    private readonly List<KeptConnection> _keptConnections = new();
    private ProjectFile? _loadedProject;
    private bool _isRestoring;
    private bool _isDirty;
    private double _viewportZoom = 1;
    private Point _viewportLocation;

    /// <summary>True when the canvas differs from the last save or open.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    /// <summary>The canvas zoom. Saved in the project; changing it is not a change to the project.</summary>
    public double ViewportZoom
    {
        get => _viewportZoom;
        set => SetProperty(ref _viewportZoom, value);
    }

    /// <summary>The canvas offset. Saved in the project; changing it is not a change to the project.</summary>
    public Point ViewportLocation
    {
        get => _viewportLocation;
        set => SetProperty(ref _viewportLocation, value);
    }

    public void MarkClean() => IsDirty = false;

    /// <summary>Empties the canvas for a new project.</summary>
    public void ClearCanvas()
    {
        _isRestoring = true;
        try
        {
            ClearGraph();
            ViewportZoom = 1;
            ViewportLocation = new Point();
        }
        finally
        {
            _isRestoring = false;
        }

        MarkClean();
    }

    /// <summary>The canvas as a project. Node ids stay, and what the file held but this version does not read goes back in.</summary>
    public ProjectFile ToProject(string projectName, string? projectFilePath, DateTime savedUtc)
    {
        var connections = Connections.Select(ToProjectConnection).ToList();
        foreach (var kept in _keptConnections.OrderBy(k => k.Index))
        {
            connections.Insert(Math.Min(kept.Index, connections.Count), kept.Connection);
        }

        return new ProjectFile
        {
            FormatVersion = ProjectSerializer.CurrentFormatVersion,
            SavedUtc = savedUtc,
            ToolVersion = ProjectSerializer.ToolVersion,
            ProjectName = projectName,
            Nodes = Nodes.Select(n => ToProjectNode(n, projectFilePath)).ToList(),
            Connections = connections,
            View = new ProjectView
            {
                Zoom = ViewportZoom,
                OffsetX = ViewportLocation.X,
                OffsetY = ViewportLocation.Y,
                Extra = _loadedProject?.View?.Extra
            },
            Extra = _loadedProject?.Extra
        };
    }

    /// <summary>
    /// Replaces the canvas with a project and validates it. A node or connection that cannot be
    /// used gives an Issues line, never an exception, and stays in the file.
    /// </summary>
    public void FromProject(ProjectFile project, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(project);

        _isRestoring = true;
        try
        {
            ClearGraph();
            _loadedProject = project;

            foreach (var saved in project.Nodes)
            {
                Nodes.Add(CreateNode(saved, projectFilePath));
            }

            for (var i = 0; i < project.Connections.Count; i++)
            {
                RestoreConnection(i, project.Connections[i]);
            }

            ViewportZoom = project.View?.Zoom ?? 1;
            ViewportLocation = new Point(project.View?.OffsetX ?? 0, project.View?.OffsetY ?? 0);
        }
        finally
        {
            _isRestoring = false;
        }

        MarkClean();
        Validate();
    }

    private void ClearGraph()
    {
        ForgetLoadedProject();
        Connections.Clear();
        Nodes.Clear();
        Issues.Clear();
        _selectedNodes.Clear();
        PendingConnection = null;
    }

    private void ForgetLoadedProject()
    {
        _loadedProject = null;
        _keptConnections.Clear();
    }

    /// <summary>Drops the kept connections of deleted nodes. True when any was dropped.</summary>
    private bool ForgetKeptConnections(IReadOnlyCollection<NodeViewModel> deleted)
    {
        var ids = deleted.Select(n => n.Id).ToHashSet();
        return _keptConnections.RemoveAll(k => ids.Contains(k.Connection.FromNode) || ids.Contains(k.Connection.ToNode)) > 0;
    }

    /// <summary>The lines that stay as long as the unknown node or kept connection does. Every rebuild of the Issues list adds them.</summary>
    private void AddProjectIssues()
    {
        foreach (var node in Nodes.Where(n => n.UnknownNodeMessage is not null))
        {
            Issues.Add(node.UnknownNodeMessage!);
        }

        foreach (var kept in _keptConnections.Where(k => k.Message is not null))
        {
            Issues.Add(kept.Message!);
        }
    }

    private NodeViewModel CreateNode(ProjectNode saved, string? projectFilePath)
    {
        if (!NodeTypesById.TryGetValue(saved.Type, out var nodeType))
        {
            return NodeFactory.CreateUnknown(saved, $"Node '{saved.Name}' has the type '{saved.Type}', which this version does not know. It is kept in the project and ignored.");
        }

        var node = NodeFactory.Create(nodeType, new Point(saved.X, saved.Y), saved.Id);
        node.LoadedFrom = saved;
        if (!string.IsNullOrEmpty(saved.Name))
        {
            node.Title = saved.Name;
        }

        try
        {
            node.IsRestoring = true;
            ApplySettings(node, saved, projectFilePath);
            return node;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return NodeFactory.CreateUnknown(saved, $"Node '{saved.Name}': the settings could not be read ({ex.Message}). It is kept in the project and ignored.");
        }
        finally
        {
            node.IsRestoring = false;
        }
    }

    private static void ApplySettings(NodeViewModel node, ProjectNode saved, string? projectFilePath)
    {
        if (saved.Settings is not { ValueKind: JsonValueKind.Object } settings)
        {
            return;
        }

        switch (node.NodeType)
        {
            case NodeType.CsvFile:
                var (csv, columns, relativePath) = ProjectNodeSettings.ReadCsvFile(settings);
                SetValue(node, "FilePath", ProjectPaths.Resolve(csv.Path, relativePath, projectFilePath));
                SetValue(node, "Separator", SeparatorPresetName(csv.Separator));
                SetValue(node, "HasHeader", csv.HasHeader ? bool.TrueString : bool.FalseString);
                SetValue(node, "SkipLines", csv.SkipLines.ToString());
                SetValue(node, "EncodingName", csv.EncodingName);
                SetValue(node, "PreviewRows", csv.PreviewRows.ToString());
                node.ColumnSelection.Mode = columns.Mode;
                node.ColumnSelection.Names.Clear();
                node.ColumnSelection.Names.AddRange(columns.Names);
                break;

            case NodeType.ParseValue:
                node.ParseValueSettings = ProjectNodeSettings.ReadParseValue(settings);
                break;

            case NodeType.DotNetObject:
                SetValue(node, "OutputType", ProjectNodeSettings.ReadOutput(settings).Kind.ToString());
                break;

            case NodeType.FileIntake:
                var (intake, intakeRelativePath) = ProjectNodeSettings.ReadFileIntake(settings);
                node.SetFileIntakeSettings(intake with { Path = ProjectPaths.Resolve(intake.Path, intakeRelativePath, projectFilePath) });
                node.IntakePlanJson = ProjectNodeSettings.ReadLayoutPlan(settings);
                break;

            case NodeType.CsvPart:
                // An unreadable selector is kept as text and gives an Issues line; it is never dropped.
                node.CsvPartSelectorJson = ProjectNodeSettings.ReadCsvPartSelector(settings);
                var partColumns = ProjectNodeSettings.ReadCsvPartColumns(settings);
                node.ColumnSelection.Mode = partColumns.Mode;
                node.ColumnSelection.Names.Clear();
                node.ColumnSelection.Names.AddRange(partColumns.Names);
                node.ColumnSplit = ProjectNodeSettings.ReadCsvPartSplit(settings);
                break;

            default:
                var values = ProjectNodeSettings.ReadLegacy(settings);
                foreach (var setting in node.Settings)
                {
                    if (values.TryGetValue(setting.Key, out var value))
                    {
                        setting.Value = setting.Kind == SettingKind.FilePath
                            ? ProjectPaths.Resolve(value, ProjectNodeSettings.ReadRelativePath(settings), projectFilePath)
                            : value;
                    }
                }

                break;
        }
    }

    private static void SetValue(NodeViewModel node, string key, string value)
        => node.Settings.Single(s => s.Key == key).Value = value;

    /// <summary>The setting row shows the preset name of a separator; the saved settings hold the separator.</summary>
    private static string SeparatorPresetName(string separator) => separator switch
    {
        "," => "Comma",
        ";" => "Semicolon",
        "tab" => "Tab",
        "|" => "Pipe",
        _ => separator
    };

    private void RestoreConnection(int index, ProjectConnection saved)
    {
        var from = Nodes.FirstOrDefault(n => n.Id == saved.FromNode);
        var to = Nodes.FirstOrDefault(n => n.Id == saved.ToNode);

        if (from?.NodeType == NodeType.Unknown || to?.NodeType == NodeType.Unknown)
        {
            _keptConnections.Add(new KeptConnection(index, saved, null));
            return;
        }

        if (from is null || to is null)
        {
            var missing = from is null ? saved.FromNode : saved.ToNode;
            Keep(index, saved, $"Project: a connection uses the node {missing}, which is not in the file. It is kept in the project and ignored.");
            return;
        }

        var source = from.Outputs.FirstOrDefault(c => c.Name == saved.FromConnector);
        var target = to.Inputs.FirstOrDefault(c => c.Name == saved.ToConnector);
        if (source is null || target is null)
        {
            var (node, key) = source is null ? (from, saved.FromConnector) : (to, saved.ToConnector);
            Keep(index, saved, $"Project: the node '{node.Title}' has no connector '{key}'. The connection is kept in the project and ignored.");
            return;
        }

        var connection = TryConnect(source, target, out var error);
        if (connection is null)
        {
            Keep(index, saved, $"Project: the connection from '{from.Title}' to '{to.Title}' was not restored. {error} It is kept in the project and ignored.");
            return;
        }

        connection.LoadedFrom = saved;
    }

    private void Keep(int index, ProjectConnection saved, string message)
        => _keptConnections.Add(new KeptConnection(index, saved, message));

    private static ProjectConnection ToProjectConnection(ConnectionViewModel connection) => new()
    {
        FromNode = connection.Source.ParentNode.Id,
        FromConnector = connection.Source.Name,
        ToNode = connection.Target.ParentNode.Id,
        ToConnector = connection.Target.Name,
        Extra = connection.LoadedFrom?.Extra
    };

    private static ProjectNode ToProjectNode(NodeViewModel node, string? projectFilePath)
    {
        var loaded = node.LoadedFrom;
        if (node.NodeType == NodeType.Unknown)
        {
            return node.Location == new Point(loaded!.X, loaded.Y)
                ? loaded
                : loaded with { X = node.Location.X, Y = node.Location.Y, Raw = null };
        }

        return new ProjectNode
        {
            Id = node.Id,
            Type = NodeTypesById.First(p => p.Value == node.NodeType).Key,
            Name = node.Title,
            X = node.Location.X,
            Y = node.Location.Y,
            Settings = WriteSettings(node, projectFilePath),
            Extra = loaded?.Extra
        };
    }

    private static JsonElement WriteSettings(NodeViewModel node, string? projectFilePath)
    {
        var loaded = node.LoadedFrom?.Settings;
        switch (node.NodeType)
        {
            case NodeType.CsvFile:
                var csv = node.GetCsvFileSettings();
                return ProjectNodeSettings.WriteCsvFile(csv, node.ColumnSelection, RelativePathFor(csv.Path, "path", loaded, projectFilePath), loaded);

            case NodeType.ParseValue:
                return ProjectNodeSettings.WriteParseValue(node.ParseValueSettings!, loaded);

            case NodeType.DotNetObject:
                return ProjectNodeSettings.WriteOutput(node.GetOutputSettings(), loaded);

            case NodeType.FileIntake:
                var intake = node.GetFileIntakeSettings();
                return ProjectNodeSettings.WriteFileIntake(intake, RelativePathFor(intake.Path, "path", loaded, projectFilePath), loaded, node.IntakePlanJson);

            case NodeType.CsvPart:
                return ProjectNodeSettings.WriteCsvPart(node.CsvPartSelectorJson, loaded, node.ColumnSelection, node.ColumnSplit);

            default:
                var filePath = node.Settings.FirstOrDefault(s => s.Kind == SettingKind.FilePath);
                var relativePath = filePath is null ? null : RelativePathFor(filePath.Value, filePath.Key, loaded, projectFilePath);
                return ProjectNodeSettings.WriteLegacy(node.Settings.Select(s => KeyValuePair.Create(s.Key, s.Value)), relativePath, loaded);
        }
    }

    /// <summary>
    /// While a file is missing, its saved relative path stays as it was: it was made when the file
    /// was there, and the absolute path may only be out of date because the project moved.
    /// </summary>
    private static string? RelativePathFor(string path, string pathProperty, JsonElement? loaded, string? projectFilePath)
    {
        if (loaded is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty(pathProperty, out var savedPath)
            && savedPath.ValueKind == JsonValueKind.String
            && savedPath.GetString() == path
            && !File.Exists(path)
            && ProjectNodeSettings.ReadRelativePath(settings) is { } savedRelativePath)
        {
            return savedRelativePath;
        }

        return ProjectPaths.ToRelative(path, projectFilePath);
    }

    private void HookDirtyTracking()
    {
        Connections.CollectionChanged += (_, _) => MarkDirty();
        Nodes.CollectionChanged += (_, _) =>
        {
            foreach (var node in Nodes)
            {
                node.PropertyChanged -= OnNodeChangedForDirty;
                node.PropertyChanged += OnNodeChangedForDirty;
                foreach (var setting in node.Settings)
                {
                    setting.ValueChanged -= OnSettingChangedForDirty;
                    setting.ValueChanged += OnSettingChangedForDirty;
                }
            }

            MarkDirty();
        };
    }

    private void OnNodeChangedForDirty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeViewModel.Location) or nameof(NodeViewModel.Title)
            or nameof(NodeViewModel.ParseValueSettings) or nameof(NodeViewModel.ColumnSelection) or nameof(NodeViewModel.IntakePlanJson)
            or nameof(NodeViewModel.CsvPartSelectorJson) or nameof(NodeViewModel.ColumnSplit))
        {
            MarkDirty();
        }
    }

    private void OnSettingChangedForDirty(object? sender, EventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (!_isRestoring)
        {
            IsDirty = true;
        }
    }
}
