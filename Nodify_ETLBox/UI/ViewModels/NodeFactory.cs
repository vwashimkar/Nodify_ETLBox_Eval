using System.IO;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Parsing;
using EtlDesigner.Engine.Pipeline;
using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.ViewModels;

/// <summary>
/// Factory for creating hardcoded node types with their specific ports and settings.
/// </summary>
public static class NodeFactory
{
    private const string SuggestedFromFileNote = "Suggested from the file. Change if wrong.";

    public static Func<NodeViewModel, CsvFileSettings, bool>? CsvPreviewHandler { get; set; }

    /// <summary>Opens the ParseValue dialog for a node. Set by MainViewModel.</summary>
    public static Func<NodeViewModel, bool>? ParseValueConfigureHandler { get; set; }
    /// <summary>
    /// Creates a CsvSource node.
    /// Output: "Rows" (PortKind.Rows)
    /// Settings: FilePath (required), Delimiter (default ",")
    /// </summary>
    public static NodeViewModel CreateCsvSource(Point location)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "CsvSource", NodeType.CsvSource)
        {
            Location = location
        };

        // Output port
        var rowsOutput = new ConnectorViewModel("Rows", PortKind.Rows, isInput: false, node);
        node.Outputs.Add(rowsOutput);

        // Settings
        node.Settings.Add(new SettingViewModel("FilePath", "File Path", SettingKind.FilePath, required: true));
        node.Settings.Add(new SettingViewModel("Delimiter", "Delimiter", SettingKind.Text, required: false, ","));

        return node;
    }

    /// <summary>
    /// Creates a FilterByCountry node.
    /// Input: "Rows" (PortKind.Rows)
    /// Output: "Rows" (PortKind.Rows)
    /// Settings: LocationColumn (default "location", required), Separator (default "|", required), Country (ComboBox, required, initially empty)
    /// </summary>
    public static NodeViewModel CreateFilterByCountry(Point location)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "FilterByCountry", NodeType.FilterByCountry)
        {
            Location = location
        };

        // Input port
        var rowsInput = new ConnectorViewModel("Rows", PortKind.Rows, isInput: true, node);
        node.Inputs.Add(rowsInput);

        // Output port
        var rowsOutput = new ConnectorViewModel("Rows", PortKind.Rows, isInput: false, node);
        node.Outputs.Add(rowsOutput);

        // Settings
        node.Settings.Add(new SettingViewModel("LocationColumn", "Location Column", SettingKind.Text, required: true, "location"));
        node.Settings.Add(new SettingViewModel("Separator", "Separator", SettingKind.Text, required: true, "|"));
        node.Settings.Add(new SettingViewModel("Country", "Country", SettingKind.ComboBox, required: true, ""));

        return node;
    }

    /// <summary>
    /// Creates a CsvFile node.
    /// Output: "Rows" (PortKind.Rows)
    /// Settings: FilePath, Separator, HasHeader, SkipLines, EncodingName, PreviewRows.
    /// </summary>
    public static NodeViewModel CreateCsvFile(Point location)
    {
        NodeViewModel? node = null;

        node = new NodeViewModel(Guid.NewGuid(), "CsvFile", NodeType.CsvFile, settings =>
        {
            if (node is null)
            {
                return false;
            }

            return CsvPreviewHandler?.Invoke(node, settings) ?? false;
        })
        {
            Location = location,
            ConfigurationStatus = "Not configured"
        };

        var rowsOutput = new ConnectorViewModel("Rows", PortKind.Rows, isInput: false, node);
        node.Outputs.Add(rowsOutput);

        var filePath = new SettingViewModel("FilePath", "File path", SettingKind.FilePath, required: true);
        var separator = new SettingViewModel("Separator", "Separator", SettingKind.ComboBox, required: true, "Comma");
        separator.ComboBoxItems.Add("Comma");
        separator.ComboBoxItems.Add("Semicolon");
        separator.ComboBoxItems.Add("Tab");
        separator.ComboBoxItems.Add("Pipe");

        var hasHeader = new SettingViewModel("HasHeader", "Header row", SettingKind.CheckBox, required: false, bool.TrueString);
        var skipLines = new SettingViewModel("SkipLines", "Skip lines", SettingKind.Number, required: true, "0");
        var encoding = new SettingViewModel("EncodingName", "Encoding", SettingKind.ComboBox, required: true, "utf-8");
        encoding.ComboBoxItems.Add("utf-8");
        encoding.ComboBoxItems.Add("windows-1252");
        encoding.ComboBoxItems.Add("utf-16");
        var previewRows = new SettingViewModel("PreviewRows", "Preview rows", SettingKind.Number, required: true, "5");

        var hasGuessedFromFirstFile = false;
        filePath.ValueChanged += (_, _) =>
        {
            if (hasGuessedFromFirstFile)
            {
                return;
            }

            var selected = filePath.Value;
            if (string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
            {
                return;
            }

            var guess = CsvDialectGuesser.Guess(selected);
            separator.Value = guess.Separator switch
            {
                ";" => "Semicolon",
                "tab" => "Tab",
                "|" => "Pipe",
                _ => "Comma"
            };

            hasHeader.BoolValue = guess.HasHeader;
            separator.InfoMessage = SuggestedFromFileNote;
            hasHeader.InfoMessage = SuggestedFromFileNote;
            hasGuessedFromFirstFile = true;
        };

        node.Settings.Add(filePath);
        node.Settings.Add(separator);
        node.Settings.Add(hasHeader);
        node.Settings.Add(skipLines);
        node.Settings.Add(encoding);
        node.Settings.Add(previewRows);

        foreach (var setting in node.Settings)
        {
            setting.ValueChanged += (_, _) => node.LoadPreviewCommand.RaiseCanExecuteChanged();

            // Any setting change may change the columns (header, separator, file).
            setting.ValueChanged += (_, _) => node.KnownColumns = null;
        }

        return node;
    }

    /// <summary>
    /// Creates a ParseValue node.
    /// Input: "Rows" (PortKind.Rows), from a CsvFile node only.
    /// Output: "Parsed" (PortKind.ParsedRows).
    /// Settings: a ParseValueSettings on the node, edited in its dialog. The settings panel shows
    /// a read-only summary.
    /// </summary>
    public static NodeViewModel CreateParseValue(Point location)
    {
        NodeViewModel? node = null;

        node = new NodeViewModel(Guid.NewGuid(), "ParseValue", NodeType.ParseValue)
        {
            Location = location,
            ConfigurationStatus = "Not configured",
            ConfigureCommand = new RelayCommand(_ =>
            {
                if (node is not null)
                {
                    ParseValueConfigureHandler?.Invoke(node);
                }
            })
        };

        node.Inputs.Add(new ConnectorViewModel("Rows", PortKind.Rows, isInput: true, node));
        node.Outputs.Add(new ConnectorViewModel("Parsed", PortKind.ParsedRows, isInput: false, node));

        var source = new SettingViewModel("SourceColumn", "Source column", SettingKind.Summary, required: false);
        var levels = new SettingViewModel("DelimiterLevels", "Delimiter levels", SettingKind.Summary, required: false);
        var slots = new SettingViewModel("Slots", "Slots", SettingKind.Summary, required: false);
        var carry = new SettingViewModel("CarryColumns", "Carry columns", SettingKind.Summary, required: false);
        node.Settings.Add(source);
        node.Settings.Add(levels);
        node.Settings.Add(slots);
        node.Settings.Add(carry);

        node.ParseValueSettingsChanged += (_, _) =>
        {
            var settings = node.ParseValueSettings;
            if (settings is null)
            {
                return;
            }

            source.Value = string.IsNullOrWhiteSpace(settings.SourceColumn) ? "(not chosen)" : settings.SourceColumn;
            levels.Value = settings.DelimiterLevels.Count.ToString();
            slots.Value = settings.Slots.Count.ToString();
            carry.Value = settings.CarryColumns.AllOtherColumns ? "All other columns"
                : settings.CarryColumns.Names.Count == 0 ? "None"
                : string.Join(", ", settings.CarryColumns.Names);
        };

        node.ParseValueSettings = DefaultParseValueSettings();

        return node;
    }

    /// <summary>
    /// Defaults from spec section 4: two levels (":" once, "|" all), trim and drop empty on,
    /// six empty slots, reject excess, allow duplicates, long shape. Carry is All because the spec
    /// carries all other columns when the file has a header; the node does not know yet whether
    /// it has one [U].
    /// </summary>
    public static ParseValueSettings DefaultParseValueSettings() => new()
    {
        SourceColumn = string.Empty,
        Slots = Enumerable.Range(1, 6).Select(i => new Slot($"S{i}", Array.Empty<string>())).ToList(),
        CarryColumns = CarryColumns.All
    };

    /// <summary>
    /// Creates a DotNetObject node: the end of CsvFile -> ParseValue -> DotNetObject.
    /// Input: "Parsed" (PortKind.ParsedRows), from a ParseValue node only. No output.
    /// Settings: "Output type", a Choice of <see cref="ObjectKinds.All"/>; kinds not built yet are
    /// shown disabled with "(comes later)".
    /// </summary>
    public static NodeViewModel CreateDotNetObject(Point location)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "DotNetObject", NodeType.DotNetObject)
        {
            Location = location,
            ConfigurationStatus = "Not configured"
        };

        node.Inputs.Add(new ConnectorViewModel("Parsed", PortKind.ParsedRows, isInput: true, node));

        var outputType = new SettingViewModel("OutputType", "Output type", SettingKind.Choice, required: true, OutputSettings.Default.Kind.ToString());
        foreach (var kind in ObjectKinds.All)
        {
            outputType.Choices.Add(new SettingChoice(
                kind.Kind.ToString(),
                kind.IsSupported ? kind.DisplayName : $"{kind.DisplayName} (comes later)",
                kind.IsSupported));
        }

        node.Settings.Add(outputType);
        return node;
    }

    /// <summary>
    /// Creates a ToJson node.
    /// Input: "Rows" (PortKind.Rows)
    /// Output: "Json" (PortKind.Json)
    /// No settings
    /// </summary>
    public static NodeViewModel CreateToJson(Point location)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "ToJson", NodeType.ToJson)
        {
            Location = location
        };

        // Input port
        var rowsInput = new ConnectorViewModel("Rows", PortKind.Rows, isInput: true, node);
        node.Inputs.Add(rowsInput);

        // Output port
        var jsonOutput = new ConnectorViewModel("Json", PortKind.Json, isInput: false, node);
        node.Outputs.Add(jsonOutput);

        return node;
    }

    /// <summary>
    /// Creates a ParseJson node.
    /// Input: "Json" (PortKind.Json)
    /// Output: "Records" (PortKind.Records)
    /// Settings: Fields (default "first_name,last_name,email", required, comma-separated)
    /// </summary>
    public static NodeViewModel CreateParseJson(Point location)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "ParseJson", NodeType.ParseJson)
        {
            Location = location
        };

        // Input port
        var jsonInput = new ConnectorViewModel("Json", PortKind.Json, isInput: true, node);
        node.Inputs.Add(jsonInput);

        // Output port
        var recordsOutput = new ConnectorViewModel("Records", PortKind.Records, isInput: false, node);
        node.Outputs.Add(recordsOutput);

        // Settings
        node.Settings.Add(new SettingViewModel("Fields", "Fields", SettingKind.Fields, required: true, "first_name,last_name,email"));

        return node;
    }

    /// <summary>
    /// Creates a ResultJson node.
    /// Input: "Records" (PortKind.Records)
    /// No settings
    /// </summary>
    public static NodeViewModel CreateResultJson(Point location)
    {
        var node = new NodeViewModel(Guid.NewGuid(), "ResultJson", NodeType.ResultJson)
        {
            Location = location
        };

        // Input port
        var recordsInput = new ConnectorViewModel("Records", PortKind.Records, isInput: true, node);
        node.Inputs.Add(recordsInput);

        return node;
    }
}
