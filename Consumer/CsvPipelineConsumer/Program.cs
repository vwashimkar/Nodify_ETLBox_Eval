using Generated;

// Usage: dotnet run --project Consumer/CsvPipelineConsumer -- <path to the CSV file>
if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("Give the CSV file to run, for example: dotnet run --project Consumer/CsvPipelineConsumer -- C:\\data\\export.csv");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // Ctrl+C stops the run

var progress = new Progress<PipelineRunProgress>(p =>
    Console.WriteLine($"  progress: read {p.Counts.Read}, out {p.Counts.Out} ({p.ElapsedMs} ms)"));

Console.WriteLine($"Running {args[0]}");
var result = await CsvPipeline.RunAsync(args[0], progress, cts.Token);

if (result.Error is not null)
{
    Console.WriteLine($"Error: {result.Error}");
}

Console.WriteLine($"Read {result.Counts.Read}, parsed {result.Counts.Parsed}, rejected {result.Counts.Rejected}, "
                  + $"flagged {result.Counts.Flagged}, out {result.Counts.Out}, cancelled {result.Cancelled}, {result.ElapsedMs} ms");

Console.WriteLine("First 5 rows:");
Console.WriteLine("  " + string.Join(", ", CsvPipeline.OutputColumns));
foreach (var row in result.Rows.Take(5))
{
    Console.WriteLine("  " + string.Join(", ", CsvPipeline.OutputColumns.Select(c => row[c])));
}

// A DLL made from a CSV part pipeline (task G6b) also says which part it read.
foreach (var name in new[] { "FileName", "Match", "Similarity", "PartNumber", "FirstLine", "LastLine" })
{
    if (result.GetType().GetProperty(name) is { } property)
    {
        Console.WriteLine($"Part {name}: {property.GetValue(result)}");
    }
}

// --all prints every row, one per line, values joined by "|" (used by the tests).
if (args.Contains("--all"))
{
    foreach (var row in result.Rows)
    {
        Console.WriteLine("row: " + string.Join("|", CsvPipeline.OutputColumns.Select(c => row[c])));
    }
}

Console.WriteLine(result.Rejects.Count == 0 ? "No rejected rows." : "Rejects:");
foreach (var reject in result.Rejects)
{
    Console.WriteLine($"  record {reject.RecordNumber}: {reject.Reason}");
}

return result.Error is null ? 0 : 2;
