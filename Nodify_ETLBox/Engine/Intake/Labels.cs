namespace EtlDesigner.Engine.Intake;

/// <summary>Names a user can change, by key. Read from a labels file in a later step.</summary>
public sealed class Labels
{
    private readonly Dictionary<string, string> _values;

    public Labels(IReadOnlyDictionary<string, string>? values = null)
        => _values = values is null ? new(StringComparer.Ordinal) : new(values, StringComparer.Ordinal);

    /// <summary>The label of a key, or null when there is none.</summary>
    public string? Find(string key) => _values.TryGetValue(key, out var value) ? value : null;
}
