using System.Buffers;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Reflection;
using System.Text;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;
using ETLBox.Json;

namespace EtlDesigner.Spike;

/// <summary>
/// G0 probes for the intake stage: findings only, nothing builds on this class.
/// Every ETLBox read has a time limit, so a hang is a finding and not a frozen run.
/// </summary>
public static class IntakeProbe
{
    private const int Copies = 20_000;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string Csv = "id,name\n1,alpha\n2,\"be,ta\"\n3,gamma\n";
    private const string Json = "[{\"id\":1,\"name\":\"alpha\"},{\"id\":2,\"name\":\"beta\"}]";

    public static async Task<string> RunAsync(string samplePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Intake probes (G0)");
        sb.AppendLine();
        sb.AppendLine($"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, ETLBox {typeof(CsvSource<>).Assembly.GetName().Version}. [P] proven by this run, [U] unproven.");
        sb.AppendLine();

        sb.AppendLine("## a. CsvSource: read from a stream");
        sb.AppendLine();
        DumpApi(sb, typeof(CsvSource<ExpandoObject>));
        sb.AppendLine();
        await TrySourceAsync(sb, "Uri not set, MemoryStream", () => CsvFrom(null, () => new MemoryStream(Encoding.UTF8.GetBytes(Csv))), 3);
        await TrySourceAsync(sb, "Uri = \"memory\", MemoryStream", () => CsvFrom("memory", () => new MemoryStream(Encoding.UTF8.GetBytes(Csv))), 3);
        await TrySourceAsync(sb, "stream that cannot seek", () => CsvFrom("memory", () => new ForwardOnlyStream(Encoding.UTF8.GetBytes(Csv))), 3);
        var embedded = Encoding.UTF8.GetBytes("text before\n" + Csv + "text after\n");
        var start = "text before\n".Length;
        await TrySourceAsync(sb, "a byte range inside a larger buffer", () => CsvFrom("memory", () => new MemoryStream(embedded, start, Encoding.UTF8.GetByteCount(Csv), false)), 3);
        sb.AppendLine();

        sb.AppendLine("## b. JsonSource: read from a stream");
        sb.AppendLine();
        DumpApi(sb, typeof(JsonSource<ExpandoObject>));
        sb.AppendLine();
        await TrySourceAsync(sb, "Uri not set, MemoryStream", () => JsonFrom(null, () => new MemoryStream(Encoding.UTF8.GetBytes(Json))), 2);
        await TrySourceAsync(sb, "Uri = \"memory\", MemoryStream", () => JsonFrom("memory", () => new MemoryStream(Encoding.UTF8.GetBytes(Json))), 2);
        await TrySourceAsync(sb, "stream that cannot seek", () => JsonFrom("memory", () => new ForwardOnlyStream(Encoding.UTF8.GetBytes(Json))), 2);
        sb.AppendLine();

        var sample = await File.ReadAllBytesAsync(samplePath);
        var bigFile = Path.Combine(Path.GetTempPath(), $"intake_probe_{Guid.NewGuid():N}.txt");
        try
        {
            var build = Stopwatch.StartNew();
            await using (var output = new FileStream(bigFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                for (var i = 0; i < Copies; i++)
                {
                    await output.WriteAsync(sample);
                }
            }

            var size = new FileInfo(bigFile).Length;
            sb.AppendLine("## c and d. Scan speed");
            sb.AppendLine();
            sb.AppendLine($"Test file: i01_sample.txt repeated {Copies:N0} times, {size:N0} bytes ({size / 1048576.0:F1} MB), written in {build.ElapsedMilliseconds:N0} ms. Each scan runs twice. [P] for this machine, this run and this build only.");
            sb.AppendLine();
            sb.AppendLine("| scan | run | ms | MB/s | result | allocated bytes | peak working set of the process MB |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            for (var run = 1; run <= 2; run++)
            {
                Measure(sb, "c. line scan with offsets", run, size, () => LineScan(bigFile));
            }

            for (var run = 1; run <= 2; run++)
            {
                Measure(sb, "d. quote-aware field counting", run, size, () => QuoteAwareScan(bigFile));
            }
        }
        finally
        {
            File.Delete(bigFile);
        }

        sb.AppendLine();
        sb.AppendLine($"Temp file deleted afterwards: {!File.Exists(bigFile)} [P].");
        return sb.ToString();
    }

    private static void DumpApi(StringBuilder sb, Type type)
    {
        sb.AppendLine($"Public API of `{Short(type)}` (reflection, this run) [P]:");
        sb.AppendLine();
        sb.AppendLine("- Constructors: " + string.Join("; ", type.GetConstructors().Select(c => $"({string.Join(", ", c.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"))})")));

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Reader", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Uri", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Resource", StringComparison.OrdinalIgnoreCase)
                        || typeof(Stream).IsAssignableFrom(p.PropertyType) || typeof(TextReader).IsAssignableFrom(p.PropertyType))
            .OrderBy(p => p.Name);
        foreach (var property in properties)
        {
            var access = (property.CanRead ? "get " : string.Empty) + (property.SetMethod is { IsPublic: true } ? "set" : string.Empty);
            sb.AppendLine($"- Property `{Short(property.PropertyType)} {property.Name}` {{ {access.Trim()} }}, declared on `{Short(property.DeclaringType!)}`");
        }

        var textReaderMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ToString()?.Contains("TextReader", StringComparison.Ordinal) == true).Select(m => m.Name).ToList();
        sb.AppendLine("- Members that mention `TextReader`: " + (textReaderMembers.Count == 0 ? "none" : string.Join(", ", textReaderMembers)));
    }

    private static string Short(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        return $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Short))}>";
    }

    private static CsvSource<ExpandoObject> CsvFrom(string? uri, Func<Stream> open)
    {
        var source = new CsvSource<ExpandoObject> { CreateStreamReader = _ => new StreamReader(open()) };
        if (uri is not null)
        {
            source.Uri = uri;
        }

        return source;
    }

    private static JsonSource<ExpandoObject> JsonFrom(string? uri, Func<Stream> open)
    {
        var source = new JsonSource<ExpandoObject> { CreateStreamReader = _ => new StreamReader(open()) };
        if (uri is not null)
        {
            source.Uri = uri;
        }

        return source;
    }

    private static async Task TrySourceAsync(StringBuilder sb, string label, Func<IDataFlowExecutableSource<ExpandoObject>> create, int expectedRows)
    {
        var line = new StringBuilder($"- {label}: ");
        try
        {
            var source = create();
            var sink = new MemoryDestination<ExpandoObject>();
            source.LinkTo(sink);
            var run = Task.Run(() => new Network(source, sink).ExecuteAsync());
            if (await Task.WhenAny(run, Task.Delay(Limit)) != run)
            {
                line.Append($"no result within {Limit.TotalSeconds:0} s, it hangs [P].");
            }
            else
            {
                await run;
                var rows = sink.Data.Select(r => string.Join("|", ((IDictionary<string, object?>)r).Values)).ToList();
                var verdict = rows.Count == expectedRows ? "works [P]" : $"wrong row count, expected {expectedRows} [P]";
                line.Append($"{verdict}; {rows.Count} rows: `{string.Join(" / ", rows)}`.");
            }
        }
        catch (Exception ex)
        {
            line.Append($"failed [P]: {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
        }

        sb.AppendLine(line.ToString());
    }

    private static void Measure(StringBuilder sb, string label, int run, long size, Func<string> scan)
    {
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var result = scan();
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        using var process = Process.GetCurrentProcess();
        var megabytesPerSecond = size / 1048576.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds);
        sb.AppendLine($"| {label} | {run} | {watch.ElapsedMilliseconds:N0} | {megabytesPerSecond:N0} | {result} | {allocated:N0} | {process.PeakWorkingSet64 / 1048576.0:N0} |");
    }

    private static FileStream OpenScan(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);

    /// <summary>Finds every line end by byte offset, LF, CRLF and CR, keeping no content.</summary>
    private static string LineScan(string path)
    {
        using var stream = OpenScan(path);
        var buffer = new byte[64 * 1024];
        var breaks = SearchValues.Create("\r\n"u8);
        long lines = 0, lastLineStart = 0, offset = 0;
        var afterCr = false;

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            var i = 0;
            while (i < read)
            {
                if (afterCr)
                {
                    afterCr = false;
                    if (buffer[i] == (byte)'\n')
                    {
                        i++;
                        lastLineStart = offset + i;
                        continue;
                    }
                }

                var next = buffer.AsSpan(i, read - i).IndexOfAny(breaks);
                if (next < 0)
                {
                    break;
                }

                i += next + 1;
                lines++;
                lastLineStart = offset + i;
                afterCr = buffer[i - 1] == (byte)'\r';
            }

            offset += read;
        }

        return $"{lines:N0} lines, last line starts at {lastLineStart:N0}";
    }

    /// <summary>Counts , ; | and tab outside double quotes for each record; a quoted line break does not end a record.</summary>
    private static string QuoteAwareScan(string path)
    {
        using var stream = OpenScan(path);
        var buffer = new byte[64 * 1024];
        Span<long> counts = stackalloc long[4];
        long records = 0, multiLineRecords = 0, longestRecord = 0, linesInRecord = 1;
        var inQuotes = false;

        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                switch (buffer[i])
                {
                    case (byte)'"':
                        inQuotes = !inQuotes;
                        break;
                    case (byte)'\n' when inQuotes:
                        linesInRecord++;
                        break;
                    case (byte)'\n':
                        records++;
                        if (linesInRecord > 1)
                        {
                            multiLineRecords++;
                        }

                        longestRecord = Math.Max(longestRecord, linesInRecord);
                        linesInRecord = 1;
                        counts.Clear();
                        break;
                    case (byte)',' when !inQuotes:
                        counts[0]++;
                        break;
                    case (byte)';' when !inQuotes:
                        counts[1]++;
                        break;
                    case (byte)'|' when !inQuotes:
                        counts[2]++;
                        break;
                    case (byte)'\t' when !inQuotes:
                        counts[3]++;
                        break;
                }
            }
        }

        return $"{records:N0} records, {multiLineRecords:N0} over several lines, longest {longestRecord:N0} lines";
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
