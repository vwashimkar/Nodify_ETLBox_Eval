namespace EtlDesigner.Domain;

public record ValidationIssue(ValidationSeverity Severity, Guid? NodeId, string Message);
