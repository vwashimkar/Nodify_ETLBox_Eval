namespace EtlDesigner.Domain;

public interface INodeTypeRegistry
{
    IReadOnlyList<NodeTypeDefinition> GetAll();

    NodeTypeDefinition? Get(string typeId);
}
