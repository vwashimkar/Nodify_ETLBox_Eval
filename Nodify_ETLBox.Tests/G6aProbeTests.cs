using System.Dynamic;
using System.IO;
using System.Text;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// Task G6a step 0: probes of the three unknowns of the G6 design. They print what they see (for
/// reports/taskG6a-probes.md) and assert only the facts the later steps rely on. Temp files only.
/// </summary>
public sealed class G6aProbeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g6a_probe_").FullName;

    public G6aProbeTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    /// <summary>A read-only stream over bytes [start, start + length) of a file; test-only stand-in for PartStream.</summary>
    private sealed class RangeStream(string path, long start, long length) : Stream
    {
        private readonly FileStream _file = Open(path, start);
        private long _left = length;

        private static FileStream Open(string path, long start)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            file.Seek(start, SeekOrigin.Begin);
            return file;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0)
            {
                return 0;
            }

            var n = _file.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed record Seen(List<string> Records, List<(int Row, int RawRow)> SkipCalls, List<string> Errors);

    private static async Task<Seen> ReadAsync(Func<Stream> open, Encoding encoding, string delimiter, bool header, int skip)
    {
        var seen = new Seen([], [], []);
        var source = new CsvSource<ExpandoObject> { CreateStreamReader = _ => new StreamReader(open(), encoding, detectEncodingFromByteOrderMarks: false) };
        source.Configuration.Delimiter = delimiter;
        source.Configuration.HasHeaderRecord = header;
        source.Configuration.ShouldSkipRecord = args =>
        {
            lock (seen)
            {
                seen.SkipCalls.Add((args.Row.Parser.Row, args.Row.Parser.RawRow));
            }

            return skip > 0 && args.Row.Parser.Row <= skip;
        };
        var step = new RowTransformation(row =>
        {
            var dict = (IDictionary<string, object?>)row;
            lock (seen)
            {
                seen.Records.Add(string.Join("|", dict.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            return null!;
        });
        var errors = new MemoryDestination<ETLBoxError>();
        var sink = new MemoryDestination<ExpandoObject>();
        source.LinkTo(step);
        step.LinkTo(sink);
        source.LinkErrorTo(errors);
        await new Network(source, step, sink).ExecuteAsync();
        seen.Errors.AddRange(errors.Data.Select(e => e.ErrorText ?? ""));
        return seen;
    }

    private void Print(string label, Seen seen)
    {
        static string Flat(string s) => s.Replace("\r", "").Replace("\n", "\\n");
        _output.WriteLine($"{label}: records [{Flat(string.Join(" / ", seen.Records))}]; ShouldSkipRecord (Parser.Row, RawRow) [{string.Join(" ", seen.SkipCalls.Select(c => $"({c.Row},{c.RawRow})"))}]; errors [{string.Join(" // ", seen.Errors.Select(Flat))}]");
    }

    /// <summary>(a) Skip lines and record numbers on a stream that starts in the middle of a file.</summary>
    [Fact]
    public async Task A_ShouldSkipRecord_OnAPartStream_CountsFromThePartStart()
    {
        const string before = "title line one\nanother line\n\n";
        const string part = "Skip me 1\nSkip me 2\nid,name\n1,alpha\n2,\"be\nta\"\n3,gamma,extra\n4,de\"l\"ta\n5,\"open,x\n6,zeta\n";
        var path = Path.Combine(_folder, "a.txt");
        File.WriteAllText(path, before + part + "\nfooter\n", new UTF8Encoding(false));
        var start = Encoding.UTF8.GetByteCount(before);
        var length = Encoding.UTF8.GetByteCount(part);

        foreach (var (header, skip) in new[] { (true, 2), (false, 2), (true, 0), (false, 0), (true, 1) })
        {
            var seen = await TestTimeout.Run(() => ReadAsync(() => new RangeStream(path, start, length), Encoding.UTF8, ",", header, skip));
            Print($"header {header}, skip {skip}", seen);
            if (skip == 2 && header)
            {
                // Parser.Row counts records from the part start (header and skipped lines included);
                // RawRow counts physical lines. "Row: N" in an error is Parser.Row of the bad record.
                Assert.Equal(["id=1|name=alpha", "id=2|name=be\nta", "id=3|name=gamma"], seen.Records);
                Assert.Equal([(1, 1), (2, 2), (3, 3), (4, 4), (5, 6), (6, 7), (7, 8), (8, 11)], seen.SkipCalls);
                Assert.Equal(2, seen.Errors.Count);
                Assert.Contains("Row: 7 ", seen.Errors[0]);
                Assert.Contains("Row: 8 ", seen.Errors[1]);
            }
        }
    }

    /// <summary>(b) A byte range of a real file: plain UTF-8, UTF-8 with a BOM (i03) and UTF-16 (temp).</summary>
    [Fact]
    public async Task B_ByteRangeOfAFile_PlainBomAndUtf16()
    {
        // i03: marker mode; the first content part must not contain the BOM.
        var i03 = IntakeTestData.Item("i03_bom");
        var scan = await new IntakeScanService(IntakeTestData.Markers).ScanAsync(i03, null, CancellationToken.None);
        var first = scan.Intake!.Containers.First(c => c.ContentStartByte is not null);
        var bytes = File.ReadAllBytes(IntakeTestData.PathOf("i03_bom"));
        _output.WriteLine($"i03: has BOM {scan.Intake.File!.HasBom}; first container {first.Format} '{first.Name}' kind {first.Kind} lines {first.ContentFirstLine}-{first.ContentLastLine} bytes {first.ContentStartByte}-{first.ContentEndByte}; byte 0..2 = {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2}");
        var firstAny = scan.Intake.Containers.Min(c => c.ContentStartByte ?? long.MaxValue);
        _output.WriteLine($"i03: smallest ContentStartByte of any container = {firstAny}");
        Assert.True(firstAny >= 3);

        // End byte: does ContentEndByte include the line break of the last line?
        var csv = scan.Intake.Containers.First(c => c.Format == "csv");
        var tail = Encoding.UTF8.GetString(bytes, (int)csv.ContentEndByte! - 4, 6).Replace("\r", "\\r").Replace("\n", "\\n");
        _output.WriteLine($"i03 csv part: bytes {csv.ContentStartByte}-{csv.ContentEndByte}; text around the end byte (end-4 .. end+2): '{tail}'");
        var text = new StreamReader(new RangeStream(IntakeTestData.PathOf("i03_bom"), first.ContentStartByte!.Value, first.ContentEndByte!.Value - first.ContentStartByte.Value), new UTF8Encoding(false), false).ReadToEnd();
        _output.WriteLine($"i03 first part text starts with U+{(int)text[0]:X4}; first 40 chars '{text[..Math.Min(40, text.Length)].Replace("\n", "\\n")}'");
        Assert.NotEqual('\uFEFF', text[0]);

        foreach (var (label, encoding) in new (string, Encoding)[] { ("utf-8 plain", new UTF8Encoding(false)), ("utf-16le BOM", new UnicodeEncoding(false, true)), ("utf-16be BOM", new UnicodeEncoding(true, true)), ("utf-8 BOM", new UTF8Encoding(true)) })
        {
            var path = Path.Combine(_folder, $"b_{label.Replace(' ', '_')}.txt");
            File.WriteAllText(path, "Instrument: X\nOperator: Y\nDate: 2026-11-10\n\nid,name,value\n1,äpfel,1.5\n2,\"b\nc\",2.5\n3,ömega,3.5\n\nEnd of report\n", encoding);
            var item = new FileIntakeFactory().CreateItem(path);
            var s = await new IntakeScanService().ScanAsync(item, null, CancellationToken.None);
            var table = s.Intake!.Containers.FirstOrDefault(c => c.Format == "csv");
            _output.WriteLine($"{label}: encoding used {s.EncodingUsed}; parts {string.Join(", ", s.Intake.Containers.Select(c => $"{c.Format} {c.ContentFirstLine}-{c.ContentLastLine} [{c.ContentStartByte},{c.ContentEndByte})"))}");
            Assert.NotNull(table);
            var enc = IntakeReader.ResolveEncoding(s.EncodingUsed!, out _)!;
            var seen = await TestTimeout.Run(() => ReadAsync(() => new RangeStream(path, table.ContentStartByte!.Value, table.ContentEndByte!.Value - table.ContentStartByte.Value), enc, ",", true, 0));
            Print($"{label} table", seen);
            Assert.Equal(["id=1|name=äpfel|value=1.5", "id=2|name=b\nc|value=2.5", "id=3|name=ömega|value=3.5"], seen.Records);
        }
    }

    /// <summary>(c) A quoted field with a line break at or near a part boundary: does the detector keep it in one part?</summary>
    [Fact]
    public async Task C_QuotedLineBreak_NearAPartBoundary()
    {
        var cases = new (string Label, string Text, long First, long Last)[]
        {
            ("last row, then blank line and text", "id,name,note\n1,a,plain\n2,b,plain\n3,c,\"two\nlines\"\n\nEnd of report\nPrinted by X\n", 1, 5),
            ("last row, continuation is a text-like line", "id,name,note\n1,a,plain\n2,b,plain\n3,c,\"see below\nEnd of report\"\n\nEnd of report\n", 1, 5),
            ("first row after a title", "Results of run 7\nid,name,note\n1,a,\"first\nsecond\"\n2,b,plain\n3,c,plain\n4,d,plain\n", 1, 7),
            ("first row after a key-value header", "Instrument: X\nOperator: Y\nDate: Z\n\nid,name,note\n1,a,\"first\nsecond\"\n2,b,plain\n3,c,plain\n", 5, 9),
            ("blank line inside the quote", "id,name,note\n1,a,plain\n2,b,\"para one\n\npara two\"\n3,c,plain\n4,d,plain\n", 1, 7),
        };

        foreach (var (label, text, firstLine, lastLine) in cases)
        {
            var item = IntakeTestData.MemoryItem(text);
            var s = await new IntakeScanService().ScanAsync(item, null, CancellationToken.None);
            var lines = text.Split('\n');
            _output.WriteLine($"{label}: {string.Join("; ", s.Intake!.Containers.Select(c => $"{c.Format} lines {c.ContentFirstLine}-{c.ContentLastLine} (skip {c.SkipLines}, header {c.HeaderGuess}) first '{lines[(int)c.ContentFirstLine! - 1]}' last '{lines[(int)c.ContentLastLine! - 1]}'"))}");
            var table = s.Intake.Containers.Single(c => c.Format == "csv");
            Assert.Equal((firstLine, lastLine), (table.ContentFirstLine!.Value, table.ContentLastLine!.Value));
        }
    }
}
