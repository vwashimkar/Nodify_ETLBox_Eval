namespace EtlDesigner.Domain;

public record ConnectionCheckResult(bool Allowed, string? Reason);
