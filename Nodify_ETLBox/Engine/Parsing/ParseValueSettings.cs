namespace EtlDesigner.Engine.Parsing;

/// <summary>How a delimiter level splits its text.</summary>
public enum SplitMode
{
    /// <summary>Split at the first occurrence only (level 1: key and rest).</summary>
    Once,

    /// <summary>Split at every occurrence (level 2: the components).</summary>
    All,

    /// <summary>Split each component at the first occurrence; the right part goes to its own column.</summary>
    Each
}

/// <summary>What to do with a record that has more components than slots.</summary>
public enum ExcessPolicy
{
    Reject,
    PairWhatFits
}

/// <summary>What to do with a record whose components repeat a name.</summary>
public enum DuplicatePolicy
{
    Allow,
    Flag,
    Reject
}

/// <summary>Output layout. Only Long is defined by the spec so far.</summary>
public enum OutputShape
{
    Long,
    Wide
}

/// <summary>
/// One delimiter level. <paramref name="SubPartColumn"/> names the output column that receives
/// the sub-part of an Each level (C09 uses "revision"); it is ignored for Once and All.
/// </summary>
public sealed record DelimiterLevel(string Delimiter, SplitMode Mode, string? SubPartColumn = null);

/// <summary>
/// The value columns for one component position, by column name. Slots are data: they may
/// differ in size, skip columns and be in any order.
/// </summary>
public sealed record Slot(string Name, IReadOnlyList<string> Columns);

/// <summary>Columns copied onto every output row: a list of names, or all other columns.</summary>
public sealed record CarryColumns(bool AllOtherColumns, IReadOnlyList<string> Names)
{
    public static CarryColumns None { get; } = new(false, Array.Empty<string>());

    /// <summary>Every column that is not the source column or a value column, in file order.</summary>
    public static CarryColumns All { get; } = new(true, Array.Empty<string>());

    public static CarryColumns Of(params string[] names) => new(false, names);
}

/// <summary>Settings of the ParseValue step. Immutable.</summary>
public sealed record ParseValueSettings
{
    public required string SourceColumn { get; init; }

    /// <summary>Level 1 must be Once (key), level 2 All (components), any further level Each.</summary>
    public IReadOnlyList<DelimiterLevel> DelimiterLevels { get; init; } = DefaultLevels;

    public bool Trim { get; init; } = true;

    public bool DropEmpty { get; init; } = true;

    public required IReadOnlyList<Slot> Slots { get; init; }

    public ExcessPolicy ExcessPolicy { get; init; } = ExcessPolicy.Reject;

    public DuplicatePolicy DuplicatePolicy { get; init; } = DuplicatePolicy.Allow;

    public CarryColumns CarryColumns { get; init; } = CarryColumns.None;

    public OutputShape OutputShape { get; init; } = OutputShape.Long;

    public static IReadOnlyList<DelimiterLevel> DefaultLevels { get; } = new[]
    {
        new DelimiterLevel(":", SplitMode.Once),
        new DelimiterLevel("|", SplitMode.All)
    };

    /// <summary>
    /// Auto-fill helper: cuts an ordered list of column names into slots of
    /// <paramref name="groupSize"/> (the manifest's auto(r01..r18;3)). A shorter last group
    /// becomes a shorter slot. Slots are named S1, S2, ...
    /// </summary>
    public static IReadOnlyList<Slot> SlotsFromGroups(IReadOnlyList<string> orderedColumns, int groupSize)
    {
        ArgumentNullException.ThrowIfNull(orderedColumns);
        if (groupSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(groupSize), "Group size must be 1 or more.");
        }

        return orderedColumns
            .Chunk(groupSize)
            .Select((columns, index) => new Slot($"S{index + 1}", columns))
            .ToList();
    }

    /// <summary>Name of the value column at a 0-based position: value_a, value_b, ..., value_z, value_aa, ...</summary>
    public static string ValueColumnName(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var letters = string.Empty;
        var n = index + 1;
        while (n > 0)
        {
            n--;
            letters = (char)('a' + n % 26) + letters;
            n /= 26;
        }

        return "value_" + letters;
    }
}
