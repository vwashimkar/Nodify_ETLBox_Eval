namespace EtlDesigner.Domain;

public class PipelineGraph
{
    private readonly List<NodeInstance> _nodes = [];
    private readonly List<Edge> _edges = [];

    public IReadOnlyList<NodeInstance> Nodes => _nodes;

    public IReadOnlyList<Edge> Edges => _edges;

    public void AddNode(NodeInstance node) => _nodes.Add(node);

    public bool RemoveNode(Guid nodeId)
    {
        var removed = _nodes.RemoveAll(n => n.Id == nodeId) > 0;
        _edges.RemoveAll(e => e.SourceNodeId == nodeId || e.TargetNodeId == nodeId);
        return removed;
    }

    public void AddEdge(Edge edge) => _edges.Add(edge);

    public bool RemoveEdge(Guid edgeId) => _edges.RemoveAll(e => e.Id == edgeId) > 0;
}
