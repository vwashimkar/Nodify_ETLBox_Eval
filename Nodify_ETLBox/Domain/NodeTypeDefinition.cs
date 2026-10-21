namespace EtlDesigner.Domain;

public record NodeTypeDefinition(
    string TypeId,
    string DisplayName,
    string Category,
    IReadOnlyList<PortDefinition> Ports,
    IReadOnlyList<NodeSettingDefinition> Settings);
