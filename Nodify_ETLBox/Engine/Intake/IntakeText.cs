using System.Globalization;

namespace EtlDesigner.Engine.Intake;

/// <summary>Wording helpers for messages a lab user reads: counts with the right plural and lists of columns.</summary>
public static class IntakeText
{
    /// <summary>"1 character", "2 characters". <paramref name="plural"/> defaults to the singular plus "s".</summary>
    public static string Count(long count, string singular, string? plural = null)
        => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>"column 1", "columns 1 and 2", "columns 2, 3 and 4". Empty for no columns.</summary>
    public static string Columns(IReadOnlyList<int> columns) => List(columns.Select(c => c.ToString(CultureInfo.InvariantCulture)).ToList(), "column", "columns");

    /// <summary>"a", "a and b", "a, b and c", after the singular or plural word.</summary>
    public static string List(IReadOnlyList<string> items, string singular, string plural) => items.Count switch
    {
        0 => string.Empty,
        1 => $"{singular} {items[0]}",
        _ => $"{plural} {string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };
}
