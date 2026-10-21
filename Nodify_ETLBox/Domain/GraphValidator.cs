namespace EtlDesigner.Domain;

public class GraphValidator
{
    public List<ValidationIssue> Validate(PipelineGraph graph, INodeTypeRegistry registry)
    {
        var issues = new List<ValidationIssue>();
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);
        var typesById = new Dictionary<Guid, NodeTypeDefinition>();

        foreach (var node in graph.Nodes)
        {
            var type = registry.Get(node.TypeId);
            if (type is null)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, node.Id, $"Unknown node type '{node.TypeId}' on node '{node.Name}'."));
                continue;
            }

            typesById[node.Id] = type;
            ValidateRequiredSettings(node, type, issues);
        }

        foreach (var edge in graph.Edges)
        {
            ValidateEdge(edge, nodesById, typesById, issues);
        }

        ValidateCycles(graph, issues);
        ValidateEndpoints(graph, typesById, issues);

        return issues;
    }

    public ConnectionCheckResult CanConnect(PipelineGraph graph, INodeTypeRegistry registry, Guid sourceNodeId,
        string sourcePortId, Guid targetNodeId, string targetPortId)
    {
        if (sourceNodeId == targetNodeId)
        {
            return new ConnectionCheckResult(false, "Source and target nodes must be different.");
        }

        var sourceNode = graph.Nodes.FirstOrDefault(n => n.Id == sourceNodeId);
        var targetNode = graph.Nodes.FirstOrDefault(n => n.Id == targetNodeId);
        if (sourceNode is null || targetNode is null)
        {
            return new ConnectionCheckResult(false, "Source or target node does not exist.");
        }

        var sourceType = registry.Get(sourceNode.TypeId);
        var targetType = registry.Get(targetNode.TypeId);
        if (sourceType is null || targetType is null)
        {
            return new ConnectionCheckResult(false, "Source or target node type is unknown.");
        }

        var sourcePort = sourceType.Ports.FirstOrDefault(p => p.Id == sourcePortId);
        var targetPort = targetType.Ports.FirstOrDefault(p => p.Id == targetPortId);
        if (sourcePort is null || targetPort is null)
        {
            return new ConnectionCheckResult(false, "Source or target port does not exist.");
        }

        if (sourcePort.Direction != PortDirection.Output || targetPort.Direction != PortDirection.Input)
        {
            return new ConnectionCheckResult(false, "A connection must go from an Output port to an Input port.");
        }

        if (graph.Edges.Any(e => e.SourceNodeId == sourceNodeId && e.SourcePortId == sourcePortId &&
                                 e.TargetNodeId == targetNodeId && e.TargetPortId == targetPortId))
        {
            return new ConnectionCheckResult(false, "This connection already exists.");
        }

        if (graph.Edges.Any(e => e.TargetNodeId == targetNodeId && e.TargetPortId == targetPortId))
        {
            return new ConnectionCheckResult(false, "The input port already has an incoming connection.");
        }

        if (CanReach(graph, targetNodeId, sourceNodeId))
        {
            return new ConnectionCheckResult(false, "This connection would create a cycle.");
        }

        return new ConnectionCheckResult(true, null);
    }

    private static bool CanReach(PipelineGraph graph, Guid fromNodeId, Guid toNodeId)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(fromNodeId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == toNodeId)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var edge in graph.Edges.Where(e => e.SourceNodeId == current))
            {
                stack.Push(edge.TargetNodeId);
            }
        }

        return false;
    }

    private static void ValidateRequiredSettings(NodeInstance node, NodeTypeDefinition type, List<ValidationIssue> issues)
    {
        foreach (var setting in type.Settings.Where(s => s.Required))
        {
            node.Settings.TryGetValue(setting.Key, out var value);
            value ??= setting.DefaultValue;

            if (value is null || (value is string text && string.IsNullOrWhiteSpace(text)))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, node.Id,
                    $"Required setting '{setting.DisplayName}' is missing on node '{node.Name}'."));
            }
        }
    }

    private static void ValidateEdge(Edge edge, Dictionary<Guid, NodeInstance> nodesById,
        Dictionary<Guid, NodeTypeDefinition> typesById, List<ValidationIssue> issues)
    {
        if (!nodesById.ContainsKey(edge.SourceNodeId) || !nodesById.ContainsKey(edge.TargetNodeId))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, edge.SourceNodeId,
                $"Edge '{edge.Id}' references a node that does not exist."));
            return;
        }

        if (!typesById.TryGetValue(edge.SourceNodeId, out var sourceType) ||
            !typesById.TryGetValue(edge.TargetNodeId, out var targetType))
        {
            return;
        }

        var sourcePort = sourceType.Ports.FirstOrDefault(p => p.Id == edge.SourcePortId);
        var targetPort = targetType.Ports.FirstOrDefault(p => p.Id == edge.TargetPortId);

        if (sourcePort is null || targetPort is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, edge.SourceNodeId,
                $"Edge '{edge.Id}' references a port that does not exist."));
            return;
        }

        if (sourcePort.Direction != PortDirection.Output || targetPort.Direction != PortDirection.Input)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, edge.SourceNodeId,
                $"Edge '{edge.Id}' must connect an Output port to an Input port."));
        }
    }

    private static void ValidateCycles(PipelineGraph graph, List<ValidationIssue> issues)
    {
        var adjacency = graph.Edges
            .GroupBy(e => e.SourceNodeId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TargetNodeId).ToList());
        var state = new Dictionary<Guid, VisitState>();
        var reported = new HashSet<Guid>();

        foreach (var node in graph.Nodes)
        {
            Visit(node.Id);
        }

        void Visit(Guid nodeId)
        {
            if (state.TryGetValue(nodeId, out var current))
            {
                if (current == VisitState.Visiting && reported.Add(nodeId))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, nodeId, "The pipeline contains a cycle."));
                }
                return;
            }

            state[nodeId] = VisitState.Visiting;
            if (adjacency.TryGetValue(nodeId, out var targets))
            {
                foreach (var target in targets)
                {
                    Visit(target);
                }
            }
            state[nodeId] = VisitState.Done;
        }
    }

    private static void ValidateEndpoints(PipelineGraph graph, Dictionary<Guid, NodeTypeDefinition> typesById,
        List<ValidationIssue> issues)
    {
        foreach (var node in graph.Nodes)
        {
            if (!typesById.TryGetValue(node.Id, out var type))
            {
                continue;
            }

            var hasInputPorts = type.Ports.Any(p => p.Direction == PortDirection.Input);
            var hasOutputPorts = type.Ports.Any(p => p.Direction == PortDirection.Output);

            if (hasOutputPorts && !hasInputPorts && !graph.Edges.Any(e => e.SourceNodeId == node.Id))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, node.Id,
                    $"Source node '{node.Name}' has no outgoing edge."));
            }

            if (hasInputPorts && !hasOutputPorts && !graph.Edges.Any(e => e.TargetNodeId == node.Id))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, node.Id,
                    $"Destination node '{node.Name}' has no incoming edge."));
            }
        }
    }

    private enum VisitState
    {
        Visiting,
        Done
    }
}
