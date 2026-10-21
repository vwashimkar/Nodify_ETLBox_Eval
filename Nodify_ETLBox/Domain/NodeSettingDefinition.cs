namespace EtlDesigner.Domain;

public record NodeSettingDefinition(string Key, string DisplayName, Type ValueType, bool Required, object? DefaultValue);
