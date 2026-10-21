namespace EtlDesigner.Domain;

public class NodeInstance
{
    public NodeInstance(Guid id, string typeId, string name, double x, double y)
    {
        Id = id;
        TypeId = typeId;
        Name = name;
        X = x;
        Y = y;
    }

    public Guid Id { get; }

    public string TypeId { get; }

    public string Name { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public Dictionary<string, object?> Settings { get; } = [];
}
