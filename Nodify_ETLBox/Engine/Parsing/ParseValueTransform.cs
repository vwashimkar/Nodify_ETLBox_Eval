namespace EtlDesigner.Engine.Parsing;

/// <summary>
/// One input record. <see cref="RecordNumber"/> is given by the caller: 1-based from the first
/// data record; header and skipped lines are not counted; a record over two lines counts once.
/// </summary>
public sealed record ParseValueRecord(int RecordNumber, IReadOnlyDictionary<string, string?> Values);

/// <summary>A rejected or flagged record and why.</summary>
public sealed record RecordIssue(int RecordNumber, string Reason);

public sealed record ParseValueResult(
    IReadOnlyList<string> OutputColumns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<RecordIssue> Rejects,
    IReadOnlyList<RecordIssue> Flagged,
    int RowsIn)
{
    public int RowsOut => Rows.Count;

    public int RowsRejected => Rejects.Count;

    /// <summary>Flagged records (a record flagged for two reasons counts once).</summary>
    public int RowsFlagged => Flagged.Select(f => f.RecordNumber).Distinct().Count();
}

/// <summary>
/// The ParseValue step (spec sections 4 and 5): composite cell to one output row per component.
/// Pure: no ETLBox, no WPF, no I/O.
/// </summary>
public static class ParseValueTransform
{
    public const string KeyColumn = "sample_id";
    public const string ComponentColumn = "component";
    public const string DuplicateComponentIds = "duplicate component ids";

    // Wording taken from expected/C03_rejects.csv and expected/C03b_flagged.csv.
    public static string ExcessReason(int componentCount, int slotCount)
        => $"component count {componentCount} exceeds {slotCount} column groups";

    public static string ExcessPairedReason(int componentCount, int slotCount)
        => ExcessReason(componentCount, slotCount) + " (paired what fits)";

    public static ParseValueResult Run(
        IReadOnlyList<string> inputColumns,
        IEnumerable<ParseValueRecord> records,
        ParseValueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inputColumns);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.OutputShape != OutputShape.Long)
        {
            throw new NotSupportedException("Wide output is not defined in the spec yet. Use the long shape.");
        }

        ValidateColumns(inputColumns, settings);

        var carried = CarriedColumns(inputColumns, settings);
        var subPartColumns = settings.DelimiterLevels.Skip(2).Select(l => l.SubPartColumn!).ToList();
        var valueWidth = settings.Slots.Count == 0 ? 0 : settings.Slots.Max(s => s.Columns.Count);

        var outputColumns = new List<string>(carried) { KeyColumn, ComponentColumn };
        outputColumns.AddRange(subPartColumns);
        outputColumns.AddRange(Enumerable.Range(0, valueWidth).Select(ParseValueSettings.ValueColumnName));

        var rows = new List<IReadOnlyList<string>>();
        var rejects = new List<RecordIssue>();
        var flagged = new List<RecordIssue>();
        var rowsIn = 0;

        foreach (var record in records)
        {
            rowsIn++;

            var cell = CompositeCellParser.Parse(
                Value(record, settings.SourceColumn), settings.DelimiterLevels, settings.Trim, settings.DropEmpty);
            if (!cell.IsValid)
            {
                rejects.Add(new RecordIssue(record.RecordNumber, cell.Error!));
                continue;
            }

            var components = cell.Components;
            var slotCount = settings.Slots.Count;
            if (components.Count > slotCount)
            {
                if (settings.ExcessPolicy == ExcessPolicy.Reject)
                {
                    rejects.Add(new RecordIssue(record.RecordNumber, ExcessReason(components.Count, slotCount)));
                    continue;
                }

                flagged.Add(new RecordIssue(record.RecordNumber, ExcessPairedReason(components.Count, slotCount)));
                components = components.Take(slotCount).ToList();
            }

            var hasDuplicates = components.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() < components.Count;
            if (hasDuplicates && settings.DuplicatePolicy == DuplicatePolicy.Reject)
            {
                rejects.Add(new RecordIssue(record.RecordNumber, DuplicateComponentIds));
                continue;
            }

            if (hasDuplicates && settings.DuplicatePolicy == DuplicatePolicy.Flag)
            {
                flagged.Add(new RecordIssue(record.RecordNumber, DuplicateComponentIds));
            }

            var carriedValues = carried.Select(c => Value(record, c)).ToList();

            // Component k uses slot k; a smaller slot leaves the remaining value columns empty.
            for (var k = 0; k < components.Count; k++)
            {
                var row = new List<string>(outputColumns.Count);
                row.AddRange(carriedValues);
                row.Add(cell.Key!);
                row.Add(components[k].Name);
                row.AddRange(components[k].SubParts);

                var slotColumns = settings.Slots[k].Columns;
                for (var v = 0; v < valueWidth; v++)
                {
                    row.Add(v < slotColumns.Count ? Value(record, slotColumns[v]) : string.Empty);
                }

                rows.Add(row);
            }
        }

        return new ParseValueResult(outputColumns, rows, rejects, flagged, rowsIn);
    }

    private static IReadOnlyList<string> CarriedColumns(IReadOnlyList<string> inputColumns, ParseValueSettings settings)
    {
        if (!settings.CarryColumns.AllOtherColumns)
        {
            return settings.CarryColumns.Names;
        }

        var valueColumns = settings.Slots.SelectMany(s => s.Columns).ToHashSet(StringComparer.Ordinal);
        return inputColumns
            .Where(c => c != settings.SourceColumn && !valueColumns.Contains(c))
            .ToList();
    }

    private static void ValidateColumns(IReadOnlyList<string> inputColumns, ParseValueSettings settings)
    {
        var known = inputColumns.ToHashSet(StringComparer.Ordinal);

        if (!known.Contains(settings.SourceColumn))
        {
            throw new ArgumentException($"The source column '{settings.SourceColumn}' is not in the file. Choose the column that holds the composite cell.");
        }

        foreach (var slot in settings.Slots)
        {
            var missing = slot.Columns.FirstOrDefault(c => !known.Contains(c));
            if (missing != null)
            {
                throw new ArgumentException($"Slot {slot.Name} uses the column '{missing}', which is not in the file. Edit the slot.");
            }
        }

        var missingCarry = settings.CarryColumns.Names.FirstOrDefault(c => !known.Contains(c));
        if (missingCarry != null)
        {
            throw new ArgumentException($"The carry column '{missingCarry}' is not in the file. Untick it or choose another column.");
        }
    }

    private static string Value(ParseValueRecord record, string column)
        => record.Values.TryGetValue(column, out var value) ? value ?? string.Empty : string.Empty;
}
