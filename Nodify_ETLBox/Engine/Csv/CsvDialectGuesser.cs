using System.Globalization;
using System.IO;

namespace EtlDesigner.Engine.Csv;

public sealed record CsvDialectSuggestion(string Separator, bool HasHeader);

public static class CsvDialectGuesser
{
    private static readonly (string Name, char Value)[] SeparatorCandidates =
    [
        (",", ','),
        (";", ';'),
        ("tab", '\t'),
        ("|", '|')
    ];

    public static CsvDialectSuggestion Guess(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new CsvDialectSuggestion(",", true);
        }

        var lines = ReadFirstLines(path, 5);
        if (lines.Count == 0)
        {
            return new CsvDialectSuggestion(",", true);
        }

        var separator = SuggestSeparator(lines);
        var hasHeader = SuggestHasHeader(lines, separator);
        return new CsvDialectSuggestion(separator, hasHeader);
    }

    private static List<string> ReadFirstLines(string path, int maxLines)
    {
        var lines = new List<string>(maxLines);
        using var reader = new StreamReader(path);

        while (lines.Count < maxLines)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static string SuggestSeparator(IReadOnlyList<string> lines)
    {
        string bestName = ",";
        double bestScore = double.NegativeInfinity;

        foreach (var candidate in SeparatorCandidates)
        {
            var counts = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => CountOutsideQuotes(l, candidate.Value))
                .ToList();

            if (counts.Count == 0)
            {
                continue;
            }

            var average = counts.Average();
            var spread = counts.Max() - counts.Min();
            var consistencyBonus = spread == 0 ? 10 : -spread;
            var nonZeroBonus = counts.Count(c => c > 0);
            var score = (average * 2) + consistencyBonus + nonZeroBonus;

            if (score > bestScore)
            {
                bestScore = score;
                bestName = candidate.Name;
            }
        }

        return bestName;
    }

    private static bool SuggestHasHeader(IReadOnlyList<string> lines, string separatorName)
    {
        if (lines.Count < 2)
        {
            return true;
        }

        var separator = separatorName.Equals("tab", StringComparison.OrdinalIgnoreCase)
            ? '\t'
            : separatorName[0];

        var first = SplitOutsideQuotes(lines[0], separator);
        var second = SplitOutsideQuotes(lines[1], separator);

        if (first.Count == 0 || second.Count == 0 || first.Count != second.Count)
        {
            return true;
        }

        var firstLikelyText = first.Count(IsLikelyHeaderToken);
        var secondLikelyData = second.Count(IsLikelyDataToken);

        return firstLikelyText >= Math.Max(1, first.Count / 2) && secondLikelyData >= Math.Max(1, second.Count / 3);
    }

    private static bool IsLikelyHeaderToken(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (IsLikelyDataToken(trimmed))
        {
            return false;
        }

        return trimmed.Any(char.IsLetter);
    }

    private static bool IsLikelyDataToken(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (LooksLikeDecimalCommaNumber(trimmed)
            && double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return true;
        }

        return trimmed.Contains('@') || trimmed.Contains('|') || trimmed.All(char.IsDigit);
    }

    private static bool LooksLikeDecimalCommaNumber(string value)
    {
        var commaIndex = value.IndexOf(',');
        if (commaIndex <= 0 || commaIndex != value.LastIndexOf(',') || commaIndex == value.Length - 1)
        {
            return false;
        }

        return value[..commaIndex].All(char.IsDigit)
               && value[(commaIndex + 1)..].All(char.IsDigit);
    }

    private static int CountOutsideQuotes(string line, char separator)
    {
        var count = 0;
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && ch == separator)
            {
                count++;
            }
        }

        return count;
    }

    private static List<string> SplitOutsideQuotes(string line, char separator)
    {
        var fields = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && ch == separator)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}