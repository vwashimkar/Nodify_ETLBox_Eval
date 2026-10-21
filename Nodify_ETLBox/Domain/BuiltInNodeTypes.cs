namespace EtlDesigner.Domain;

public static class BuiltInNodeTypes
{
    private const string SourceCategory = "Source";
    private const string DestinationCategory = "Destination";

    private static readonly PortDefinition InputPort = new("in", "Input", PortDirection.Input);
    private static readonly PortDefinition OutputPort = new("out", "Output", PortDirection.Output);

    public static IReadOnlyList<NodeTypeDefinition> All { get; } =
    [
        Source("CsvSource", "CSV Source", [
            Required("FilePath", "File path", typeof(string)),
            Optional("Delimiter", "Delimiter", typeof(string), ","),
            Optional("HasHeader", "Has header", typeof(bool), true)]),
        Destination("CsvDestination", "CSV Destination", [
            Required("FilePath", "File path", typeof(string)),
            Optional("Delimiter", "Delimiter", typeof(string), ","),
            Optional("HasHeader", "Has header", typeof(bool), true)]),
        Source("JsonSource", "JSON Source", [
            Required("FilePath", "File path", typeof(string))]),
        Destination("JsonDestination", "JSON Destination", [
            Required("FilePath", "File path", typeof(string))])
    ];

    private static NodeTypeDefinition Source(string typeId, string displayName, NodeSettingDefinition[] settings) =>
        new(typeId, displayName, SourceCategory, [OutputPort], settings);

    private static NodeTypeDefinition Destination(string typeId, string displayName, NodeSettingDefinition[] settings) =>
        new(typeId, displayName, DestinationCategory, [InputPort], settings);

    private static NodeSettingDefinition Required(string key, string displayName, Type valueType) =>
        new(key, displayName, valueType, true, null);

    private static NodeSettingDefinition Optional(string key, string displayName, Type valueType, object? defaultValue) =>
        new(key, displayName, valueType, false, defaultValue);
}

