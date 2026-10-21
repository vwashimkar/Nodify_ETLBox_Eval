namespace EtlDesigner.Domain;

public class InMemoryNodeTypeRegistry : INodeTypeRegistry
{
    private readonly Dictionary<string, NodeTypeDefinition> _types;

    public InMemoryNodeTypeRegistry(IEnumerable<NodeTypeDefinition> definitions)
    {
        _types = definitions.ToDictionary(d => d.TypeId);
    }

    public IReadOnlyList<NodeTypeDefinition> GetAll() => _types.Values.ToList();

    public NodeTypeDefinition? Get(string typeId) => _types.GetValueOrDefault(typeId);
}
