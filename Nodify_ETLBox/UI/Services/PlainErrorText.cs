using EtlDesigner.Engine.Intake;

namespace EtlDesigner.UI.Services;

/// <summary>A message for a lab user: one plain sentence, and the engine's own text for the "Details" expander.</summary>
public sealed record PlainError(string Sentence, string Details);

/// <summary>
/// Turns the engine's messages into sentences a lab user understands (task G7a, the guided builder). The
/// technical text is kept as <see cref="PlainError.Details"/>. Matching uses the locator's failure kind when
/// there is one, else stable parts of the engine's wording. Unknown text comes back as it is. No WPF types.
/// </summary>
public static class PlainErrorText
{
    public const string BadRow = "Some rows of the table are broken (a quote or separator in the wrong place), so they were left out. The other rows are fine.";
    public const string BadRead = "The table could not be read: a row is broken (a quote or separator in the wrong place), or the table includes lines that are not part of it. Pick another table, or fix the layout in Intake Studio.";
    public const string NotFound = "The table was not found in this file. Check that the file comes from the same instrument and the same export settings.";
    public const string NotSameInstrument = "This file does not look like the sample file. Check that it comes from the same instrument and the same export settings.";
    public const string ColumnCountChanged = "The table in this file has a different number of columns than the sample file. The instrument's export settings may have changed.";
    public const string HeaderChanged = "The column names in this file are different from the sample file. The instrument's export settings may have changed.";
    public const string SeveralCopies = "This file has the same table more than once, and the reader takes exactly one. Use a file with one copy of the table.";
    public const string NotATable = "Where the table should be, this file has something that is not a table. Check that the file comes from the same instrument.";
    public const string OtherSeparator = "The table in this file uses a different separator than the sample file. The instrument's export settings may have changed.";
    public const string TooLarge = "The file is larger than the size limit. Choose a smaller file, or raise the limit in the Advanced view.";
    public const string YamlMissing = "A helper file is missing next to the reader: copy YamlDotNet.dll into the same folder as the reader DLL and try again.";
    public const string Licence = "The ETLBox licence stopped the reading after about 5,000 rows. A valid ETLBox licence is needed to read larger files; the rows read so far are kept.";
    public const string FileMissing = "The file was not found. It may have been moved or renamed; choose it again.";
    public const string FileLocked = "The file is open in another program. Close it there and try again.";
    public const string FileUnreadable = "The file cannot be read with your access rights. Ask for read access and try again.";
    public const string Cancelled = "Stopped. Nothing was changed.";

    /// <summary>The plain sentence for <paramref name="technical"/>; <paramref name="kind"/>, when known, decides first.</summary>
    public static PlainError Map(string technical, CsvPartFailure? kind = null)
    {
        technical ??= string.Empty;
        var sentence = kind switch
        {
            CsvPartFailure.FileMissing => FileMissing,
            CsvPartFailure.FileLocked => FileLocked,
            CsvPartFailure.FileUnreadable => FileUnreadable,
            CsvPartFailure.Cancelled => Cancelled,
            CsvPartFailure.NoMatch => NotSameInstrument,
            CsvPartFailure.PartNotPlaced => NotFound,
            CsvPartFailure.SeveralParts => SeveralCopies,
            CsvPartFailure.NotATable => NotATable,
            CsvPartFailure.DelimiterDiffers => OtherSeparator,
            CsvPartFailure.ColumnCountDiffers => ColumnCountChanged,
            CsvPartFailure.ColumnNamesDiffer => HeaderChanged,
            _ => null
        } ?? ByText(technical);
        return new PlainError(sentence ?? technical, technical);
    }

    /// <summary>The plain sentence of a reject reason (one row); the reason itself when it is not known.</summary>
    public static string RowReason(string reason)
        => Contains(reason, "CSV structure of this record is broken") ? BadRow : reason;

    private static string? ByText(string text)
    {
        if (Contains(text, "licence limit") || Contains(text, "licence stopped") || Contains(text, "trial key")) return Licence;
        if (Contains(text, "YamlDotNet")) return YamlMissing;
        if (Contains(text, "above the limit of") && Contains(text, "MB")) return TooLarge;
        if (Contains(text, "does not look like the example")) return NotSameInstrument;
        if (Contains(text, "was not found in this file")) return NotFound;
        if (Contains(text, "CSV parts like")) return SeveralCopies;
        if (Contains(text, "columns in this file, but the pipeline expects")) return ColumnCountChanged;
        if (Contains(text, "column names of") && Contains(text, "differ")) return HeaderChanged;
        if (Contains(text, "is not a table, but the pipeline expects")) return NotATable;
        if (Contains(text, "table, but the pipeline expects")) return OtherSeparator;
        if (Contains(text, "Bad data") || Contains(text, "CSV structure of this record is broken") || Contains(text, "BadDataException")) return BadRead;
        if (Contains(text, "file was not found")) return FileMissing;
        if (Contains(text, "open in another program")) return FileLocked;
        return null;
    }

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
