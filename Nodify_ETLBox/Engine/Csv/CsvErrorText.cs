using System.Text.RegularExpressions;

namespace EtlDesigner.Engine.Csv;

public static class CsvErrorText
{
    private static readonly Regex RowNumberRegex = new(@"Row:\s*(\d+)", RegexOptions.CultureInvariant);

    public static bool TryGetRowNumber(string? errorText, out int rowNumber)
    {
        rowNumber = 0;

        if (string.IsNullOrWhiteSpace(errorText))
        {
            return false;
        }

        var match = RowNumberRegex.Match(errorText);
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, out rowNumber);
    }
}
