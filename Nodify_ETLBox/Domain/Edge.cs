namespace EtlDesigner.Domain;

public record Edge(Guid Id, Guid SourceNodeId, string SourcePortId, Guid TargetNodeId, string TargetPortId);
