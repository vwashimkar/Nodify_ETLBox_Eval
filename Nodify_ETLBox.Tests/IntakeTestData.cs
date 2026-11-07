using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;

namespace Nodify_ETLBox.Tests;

/// <summary>The files in SampleData/TestData/intake and helpers to read them.</summary>
internal static class IntakeTestData
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "SampleData", "TestData", "intake");

    public static readonly string[] MarkerFiles =
    [
        "i01_sample", "i02_crlf", "i03_bom", "i04_unterminated_last",
        "i05_missing_end_before_next_begin", "i06_stray_end", "i07_edge_cases"
    ];

    public static readonly IntakeSettings Markers = new() { Mode = IntakeMode.Markers };

    public static string PathOf(string name) => Path.Combine(Folder, name + ".txt");

    public static IntakeItem Item(string name) => new FileIntakeFactory().CreateItem(PathOf(name));

    public static IntakeItem MemoryItem(byte[] bytes)
        => new(Guid.NewGuid(), "memory", "memory.txt", bytes.Length, DateTime.UtcNow, () => new MemoryStream(bytes, writable: false));

    public static IntakeItem MemoryItem(string text) => MemoryItem(Encoding.UTF8.GetBytes(text));

    public static IntakeResult ReadMarkers(IntakeItem item)
        => IntakeReader.Read(item, Markers, null, CancellationToken.None);

    public static IntakeResult ReadMarkers(string name) => ReadMarkers(Item(name));

    public static List<string[]> ReadCsv(string path)
    {
        var rows = new List<string[]>();
        foreach (var line in File.ReadAllLines(path))
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"' && inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }

        return rows;
    }

    /// <summary>Splits a file into lines without the code under test, for the checks against it.</summary>
    public static List<(string Text, long Start, long End)> SplitLines(string name)
    {
        var bytes = File.ReadAllBytes(PathOf(name));
        var lines = new List<(string, long, long)>();
        var start = bytes.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var position = start;

        while (position < bytes.Length)
        {
            if (bytes[position] != (byte)'\n' && bytes[position] != (byte)'\r')
            {
                position++;
                continue;
            }

            lines.Add((Encoding.UTF8.GetString(bytes, start, position - start), start, position));
            position += bytes[position] == (byte)'\r' && position + 1 < bytes.Length && bytes[position + 1] == (byte)'\n' ? 2 : 1;
            start = position;
        }

        if (start < bytes.Length)
        {
            lines.Add((Encoding.UTF8.GetString(bytes, start, bytes.Length - start), start, bytes.Length));
        }

        return lines;
    }
}
