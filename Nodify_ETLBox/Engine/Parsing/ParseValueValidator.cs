namespace EtlDesigner.Engine.Parsing;

/// <summary>
/// Checks ParseValue settings before a test or a run. Each message says what is wrong and what to
/// do. Pure: no ETLBox, no WPF. Column checks run only when the kept columns are known.
/// </summary>
public static class ParseValueValidator
{
    public const string NoSourceColumn = "Choose the source column (the column that holds the composite cell).";
    public const string NoDelimiterLevels = "Add the delimiter levels: level 1 splits off the key, level 2 splits the components.";
    public const string WrongLevelOrder = "Level 1 must split once (key) and level 2 must split all (components). Change the level modes.";
    public const string NoSlots = "Add at least one slot, then mark its columns in the grid.";

    public static string SourceNotKept(string column)
        => $"The source column '{column}' is not among the kept columns of the CsvFile node. Choose another column, or keep it in Data Studio.";

    public static string EmptyDelimiter(int level)
        => $"Delimiter level {level} has no delimiter text. Type the character that separates the parts.";

    public static string EachWithoutColumn(int level)
        => $"Delimiter level {level} splits each component but has no column name. Enter the name of the column for the sub-part.";

    /// <summary>
    /// One message for all empty slots. An empty slot is allowed while another slot has columns,
    /// because a record may have fewer components than slots.
    /// </summary>
    public const string NoSlotHasColumns = "no slot has any columns yet. Open Configure and assign columns, or use Auto-fill.";

    public static string SlotColumnNotKept(string slot, string column)
        => $"Slot {slot} uses the column '{column}', which is not among the kept columns. Edit the slot, or keep the column in Data Studio.";

    public static string DuplicateSlotName(string slot)
        => $"Two or more slots are named '{slot}'. Give each slot its own name.";

    public static string CarryNotKept(string column)
        => $"The carry column '{column}' is not among the kept columns. Untick it, or keep the column in Data Studio.";

    public static IReadOnlyList<string> Validate(ParseValueSettings settings, IReadOnlyList<string>? keptColumns)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var messages = new List<string>();
        var kept = keptColumns?.ToHashSet(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(settings.SourceColumn))
        {
            messages.Add(NoSourceColumn);
        }
        else if (kept is not null && !kept.Contains(settings.SourceColumn))
        {
            messages.Add(SourceNotKept(settings.SourceColumn));
        }

        var levels = settings.DelimiterLevels;
        if (levels.Count == 0)
        {
            messages.Add(NoDelimiterLevels);
        }
        else if (levels.Count < 2 || levels[0].Mode != SplitMode.Once || levels[1].Mode != SplitMode.All)
        {
            messages.Add(WrongLevelOrder);
        }

        for (var i = 0; i < levels.Count; i++)
        {
            if (string.IsNullOrEmpty(levels[i].Delimiter))
            {
                messages.Add(EmptyDelimiter(i + 1));
            }

            if (levels[i].Mode == SplitMode.Each && string.IsNullOrWhiteSpace(levels[i].SubPartColumn))
            {
                messages.Add(EachWithoutColumn(i + 1));
            }
        }

        if (settings.Slots.Count == 0)
        {
            messages.Add(NoSlots);
        }

        else if (settings.Slots.All(s => s.Columns.Count == 0))
        {
            messages.Add(NoSlotHasColumns);
        }

        if (kept is not null)
        {
            foreach (var slot in settings.Slots)
            {
                messages.AddRange(slot.Columns.Where(c => !kept.Contains(c)).Distinct().Select(c => SlotColumnNotKept(slot.Name, c)));
            }
        }

        messages.AddRange(settings.Slots
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => DuplicateSlotName(g.Key)));

        if (kept is not null && !settings.CarryColumns.AllOtherColumns)
        {
            messages.AddRange(settings.CarryColumns.Names.Where(c => !kept.Contains(c)).Distinct().Select(CarryNotKept));
        }

        return messages.Distinct().ToList();
    }
}
