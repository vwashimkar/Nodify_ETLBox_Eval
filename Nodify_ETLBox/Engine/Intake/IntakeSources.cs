using System.IO;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// The Abstract Factory for one source kind (plan section 9): file now; folder watch, TCP port and
/// IoT subscriber later, each as its own task. The registry maps a kind to its factory, so the rest
/// of the code works on an <see cref="IntakeItem"/> and never asks where it came from. The trigger,
/// settings schema and validator of a source join this interface when the second kind is built. A
/// source that cannot seek spools to a temp file first, so items always have a size and offsets.
/// </summary>
public interface IIntakeFactory
{
    string SourceKind { get; }

    /// <summary>Makes the item for a location. Never throws for a bad location: the read reports it.</summary>
    IntakeItem CreateItem(string location);
}

/// <summary>The file source. The location is a file path.</summary>
public sealed class FileIntakeFactory : IIntakeFactory
{
    public const string Kind = "file";

    public string SourceKind => Kind;

    public IntakeItem CreateItem(string location)
    {
        var size = File.Exists(location) ? new FileInfo(location).Length : 0;

        return new IntakeItem(
            Guid.NewGuid(),
            Kind,
            Path.GetFileName(location),
            size,
            DateTime.UtcNow,
            () => new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan));
    }
}

/// <summary>Finds the factory for a source kind.</summary>
public sealed class IntakeFactoryRegistry
{
    private readonly Dictionary<string, IIntakeFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public IntakeFactoryRegistry(IEnumerable<IIntakeFactory> factories)
    {
        foreach (var factory in factories)
        {
            _factories[factory.SourceKind] = factory;
        }
    }

    public static IntakeFactoryRegistry Default { get; } = new([new FileIntakeFactory()]);

    public IIntakeFactory? Find(string sourceKind, out string? error)
    {
        if (_factories.TryGetValue(sourceKind, out var factory))
        {
            error = null;
            return factory;
        }

        var known = string.Join(", ", _factories.Keys.Order(StringComparer.OrdinalIgnoreCase));
        error = $"Source kind '{sourceKind}' is not known. Use one of: {known}.";
        return null;
    }
}
