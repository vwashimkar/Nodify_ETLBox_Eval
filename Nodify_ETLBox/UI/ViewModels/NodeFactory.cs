using System.Windows;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.ViewModels;

/// <summary>
/// Factory for creating hardcoded node types with their specific ports and settings.
/// </summary>
public static class NodeFactory
{
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
