using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5a step 3: the lineage text file. Temp folders only; nothing is written next to the test program.</summary>
public class TextFileLineageLogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("g5a_lineage_").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private string Sub(string name) => Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;

    private TextFileLineageLog Log(double maxMb = 10, string? folder = null, string? fallback = null)
        => new(new LineageSettings { Folder = folder ?? Sub("app"), MaxMb = maxMb }, fallback ?? Path.Combine(_folder, "fallback"));

    private static LineageEvent Event(string name = "edit.renamed", object? value = null)
        => new(name, "intake-1", "sample.txt", "abc123", new Dictionary<string, object?> { ["to"] = value ?? "Results", ["container"] = 2 });

    private static List<JsonElement> Lines(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("\n", text);
        return text.TrimEnd('\n').Split('\n').Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public void DefaultPath_IsLineageLogInTheGivenFolder_OrTheProgramFolder()
    {
        Assert.Equal(Path.Combine(_folder, "lineage.log"), LineageLocation.DefaultPath(_folder));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "lineage.log"), LineageLocation.DefaultPath());
    }

    [Fact]
    public void EachEvent_IsOneLine_WithTheFieldNames()
    {
        var log = Log();
        log.Write(Event());
        log.Write(Event("edit.split"));

        Assert.Equal(Path.Combine(_folder, "app", "lineage.log"), log.FilePath);
        Assert.Null(log.Note);
        var bytes = File.ReadAllBytes(log.FilePath!);
        Assert.False(bytes.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF]), "no BOM");

        var lines = Lines(log.FilePath!);
        Assert.Equal(2, lines.Count);
        Assert.Equal(["id", "utc", "event", "intakeId", "file", "sha256", "details"], lines[0].EnumerateObject().Select(p => p.Name));
        Assert.True(Guid.TryParse(lines[0].GetProperty("id").GetString(), out _));
        Assert.Equal(DateTimeKind.Utc, DateTime.Parse(lines[0].GetProperty("utc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).Kind);
        Assert.Equal("edit.split", lines[1].GetProperty("event").GetString());
        Assert.Equal("abc123", lines[1].GetProperty("sha256").GetString());
        Assert.Equal(2, lines[1].GetProperty("details").GetProperty("container").GetInt32());
    }

    [Fact]
    public void AFullPath_IsWrittenAsTheFileNameOnly()
    {
        var log = Log();
        log.Write(Event() with { File = Path.Combine(_folder, "data", "export.csv") });
        var text = File.ReadAllText(log.FilePath!);
        Assert.Equal("export.csv", Lines(log.FilePath!)[0].GetProperty("file").GetString());
        Assert.DoesNotContain(_folder, text);
        Assert.DoesNotContain(_folder.Replace("\\", "\\\\"), text);
    }

    [Fact]
    public void LongTexts_AreCutTo80Characters()
    {
        var log = Log();
        log.Write(Event(value: new string('n', 300)));
        Assert.Equal(80, Lines(log.FilePath!)[0].GetProperty("details").GetProperty("to").GetString()!.Length);
    }

    [Fact]
    public void ABigFile_IsRotated_BeforeTheNextAppend()
    {
        var log = Log(maxMb: 0.0001); // about 105 bytes
        log.Write(Event("first"));
        log.Write(Event("second"));
        log.Write(Event("third"));

        var folder = Path.GetDirectoryName(log.FilePath!)!;
        Assert.Equal(["third"], Lines(log.FilePath!).Select(l => l.GetProperty("event").GetString()));
        Assert.Equal(["second"], Lines(Path.Combine(folder, "lineage.1.log")).Select(l => l.GetProperty("event").GetString()));
    }

    [Fact]
    public void AnUnwritableFolder_FallsBack_AndSaysSo()
    {
        var blocker = Path.Combine(_folder, "a-file");
        File.WriteAllText(blocker, "x");
        var log = Log(folder: Path.Combine(blocker, "sub"), fallback: Path.Combine(_folder, "EtlDesigner"));
        log.Write(Event());

        Assert.Equal(Path.Combine(_folder, "EtlDesigner", "lineage.log"), log.FilePath);
        Assert.Equal("The lineage file is kept in EtlDesigner because the program folder is not writable.", log.Note);
        Assert.Single(Lines(log.FilePath!));
    }

    [Fact]
    public void NothingWritable_KeepsTheLastEventsInMemory_AndNeverThrows()
    {
        var blocker = Path.Combine(_folder, "a-file");
        File.WriteAllText(blocker, "x");
        var log = new TextFileLineageLog(new LineageSettings { Folder = Path.Combine(blocker, "one"), MaxMemoryEvents = 5 }, Path.Combine(blocker, "two"));
        for (var i = 0; i < 8; i++)
        {
            log.Write(Event("e" + i));
        }

        Assert.Null(log.FilePath);
        Assert.Contains("kept in memory", log.Note);
        Assert.Equal(["e3", "e4", "e5", "e6", "e7"], log.MemoryLines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("event").GetString()));
    }

    [Fact]
    public void EightThreads_Writing200EventsEach_Give1600ValidLines()
    {
        var log = Log();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            for (var i = 0; i < 200; i++)
            {
                log.Write(Event($"t{t}.{i}"));
            }
        });

        var lines = Lines(log.FilePath!);
        Assert.Equal(1600, lines.Count);
        Assert.Equal(1600, lines.Select(l => l.GetProperty("event").GetString()).Distinct().Count());
    }

    [Fact]
    public void AnotherWriterAppending_IsTolerated_AndNoLineIsHalfWritten()
    {
        var log = Log();
        log.Write(Event("first"));
        using (var other = new FileStream(log.FilePath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            other.Write("{\"event\":\"other\"}\n"u8);
            other.Flush();
            log.Write(Event("while-open"));
        }

        Assert.Equal(["first", "other", "while-open"], Lines(log.FilePath!).Select(l => l.GetProperty("event").GetString()));
    }

    [Fact]
    public async Task ASessionScenario_GivesTheEventsInOrder_WithoutPathsOrNames_AndLeavesTheFileUnchanged()
    {
        var input = Path.Combine(Sub("input"), "sample.txt");
        File.WriteAllText(input, IntakeEditSessionTests.Sample, new UTF8Encoding(false));
        var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));

        var log = Log();
        var intakeId = Guid.NewGuid().ToString();
        var item = new FileIntakeFactory().CreateItem(input);
        var scan = await new IntakeScanService(null, log).ScanAsync(item, null, CancellationToken.None, intakeId);
        var session = new IntakeEditSession(item, scan, log, intakeId);
        Assert.True(session.Rename(0, "Results").Ok);
        Assert.True(session.Split(0, 4).Ok);
        Assert.True(session.Undo().Ok);
        Assert.True(session.Redo().Ok);
        Assert.True(session.Merge(1).Ok);
        var plan = IntakePlan.FromSession(session, scan);

        var changedPath = Path.Combine(Sub("changed"), "sample.txt");
        File.WriteAllText(changedPath, IntakeEditSessionTests.Sample.Replace("alpha", "ALPHA"), new UTF8Encoding(false));
        var changedItem = new FileIntakeFactory().CreateItem(changedPath);
        var changedScan = await new IntakeScanService(null, log).ScanAsync(changedItem, null, CancellationToken.None, intakeId);
        Assert.Null(IntakeEditSession.FromPlan(changedItem, changedScan, plan, log, intakeId, out var message));
        Assert.Equal(IntakePlan.ChangedFileMessage, message);

        var lines = Lines(log.FilePath!);
        Assert.Equal(
            ["intake.scanned", "edit.renamed", "edit.split", "edit.undone", "edit.redone", "edit.merged", "plan.saved", "intake.scanned", "plan.rejected"],
            lines.Select(l => l.GetProperty("event").GetString()));
        Assert.All(lines, l => Assert.Equal(intakeId, l.GetProperty("intakeId").GetString()));
        Assert.All(lines, l => Assert.Equal("sample.txt", l.GetProperty("file").GetString()));
        Assert.All(lines, l => Assert.Equal(64, l.GetProperty("sha256").GetString()!.Length));

        var scanned = lines[0].GetProperty("details");
        Assert.Equal(3, scanned.GetProperty("containers").GetInt32());
        Assert.Equal("AutoDetect", scanned.GetProperty("mode").GetString());

        var split = lines[2].GetProperty("details");
        Assert.Equal(1, split.GetProperty("before")[0].GetProperty("container").GetInt32());
        Assert.Equal(5, split.GetProperty("before")[0].GetProperty("lastLine").GetInt32());
        Assert.Equal(4, split.GetProperty("after")[1].GetProperty("firstLine").GetInt32());
        Assert.Equal("edit.split", lines[3].GetProperty("details").GetProperty("step").GetString());
        Assert.Equal(IntakePlan.ChangedFileMessage, lines[8].GetProperty("details").GetProperty("reason").GetString());

        var text = File.ReadAllText(log.FilePath!);
        foreach (var secret in new[] { _folder, _folder.Replace("\\", "\\\\"), Environment.UserName, Environment.MachineName, "alpha" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))));
        File.Delete(input); // nothing keeps the input open
    }
}
