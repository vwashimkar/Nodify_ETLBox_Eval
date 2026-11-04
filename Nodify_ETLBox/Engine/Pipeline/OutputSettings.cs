namespace EtlDesigner.Engine.Pipeline;

/// <summary>The .NET object the DotNetObject node hands to the host app.</summary>
public enum ObjectKind
{
    /// <summary>List&lt;Dictionary&lt;string, string&gt;&gt;: one dictionary per output row, keyed by column name.</summary>
    ListOfStringDictionary,

    /// <summary>A list of a generated class. Not built yet.</summary>
    TypedClassList,

    /// <summary>A System.Data.DataTable. Not built yet.</summary>
    DataTable
}

/// <summary>One output kind as shown to the user. Unsupported kinds are listed but refused.</summary>
public sealed record ObjectKindInfo(ObjectKind Kind, string DisplayName, bool IsSupported);

/// <summary>
/// The output kinds in one place. Adding a kind later means one line here and one case in
/// <see cref="PipelineRunner"/> where the rows are shaped.
/// </summary>
public static class ObjectKinds
{
    public const string NotYetSupported = "This output type comes later.";

    public static IReadOnlyList<ObjectKindInfo> All { get; } = new[]
    {
        new ObjectKindInfo(ObjectKind.ListOfStringDictionary, "List<Dictionary<string, string>>", IsSupported: true),
        new ObjectKindInfo(ObjectKind.TypedClassList, "List<T> of a generated class", IsSupported: false),
        new ObjectKindInfo(ObjectKind.DataTable, "DataTable", IsSupported: false)
    };

    public static ObjectKindInfo Get(ObjectKind kind) => All.First(k => k.Kind == kind);
}

/// <summary>Settings of the DotNetObject node. Immutable.</summary>
public sealed record OutputSettings(ObjectKind Kind = ObjectKind.ListOfStringDictionary)
{
    public static OutputSettings Default { get; } = new();

    /// <summary>Messages for the DotNetObject node (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(OutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ObjectKinds.All.Any(k => k.Kind == settings.Kind && k.IsSupported)
            ? Array.Empty<string>()
            : new[] { ObjectKinds.NotYetSupported };
    }
}
