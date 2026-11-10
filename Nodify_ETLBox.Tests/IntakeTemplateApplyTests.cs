using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5c1 step 2c: save a layout as a template, suggest templates, apply one. Temp folders only.</summary>
public sealed class IntakeTemplateApplyTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Directory.CreateTempSubdirectory("g5c1_templates_").FullName;

    public IntakeTemplateApplyTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private FileTemplateStore Store() => new(new TemplateSettings { Folder = Path.Combine(_folder, "templates") }, Path.Combine(_folder, "fallback"));

    /// <summary>The instrument file of the 2a tests with a footer of two paragraphs; the second starts with "Signature section".</summary>
    private static string Report(int rows, string section = "Signature section", int seed = 1)
        => IntakeTemplateTests.Instrument(rows, seed: seed) + $"\n{section}\nSigned: operator {seed}\nApproved: lead {seed}\n";

    private static async Task<IntakeEditSession> SessionAsync(string text, ILineageLog? log = null)
        => await IntakeEditSessionTests.SessionAsync(text, log: log);

    private static List<string> Lines(IntakeEditSession s, ContainerInfo c)
        => IntakeReader.ReadLines(s.Item, c.ContentStartByte!.Value, c.ContentEndByte!.Value, 1000, s.EncodingName).Lines.ToList();

    private static List<(string, string?, HeaderGuess?, int, KeyValueForm?, string)> Shape(IntakeEditSession s)
        => s.Containers.Select(c => (c.Format, c.Delimiter, c.HeaderGuess, c.SkipLines, c.KeyValueForm, c.Name)).ToList();

    /// <summary>Splits the footer at "Signature section" and names the parts; returns the session.</summary>
    private static async Task<IntakeEditSession> EditedReportAsync(int rows, ILineageLog? log = null)
    {
        var s = await SessionAsync(Report(rows), log);
        var footer = s.Containers.Count - 1;
        Assert.Equal("text", s.Containers[footer].Format);
        var line = s.Containers[footer].ContentFirstLine!.Value + Lines(s, s.Containers[footer]).IndexOf("Signature section");
        Assert.True(s.Split(footer, line).Ok);
        Assert.True(s.Rename(0, "Run header").Ok);
        Assert.True(s.Rename(1, "Results").Ok);
        Assert.True(s.SetTableOptions(1, header: "Yes").Ok);
        Assert.True(s.Rename(s.Containers.Count - 1, "Signatures").Ok);
        return s;
    }

    /// <summary>Tests (6), (9): a template from an edited i01 on a copy of i01 with one more table row; Undo is one step.</summary>
    [Fact]
    public async Task EditedI01_AppliedToI01WithMoreRows_GivesTheEditedLayout_AndUndoIsOneStep()
    {
        var s = await IntakeEditSessionTests.FileSessionAsync("i01_sample", IntakeMode.AutoDetect);
        var csv = s.Containers.ToList().FindIndex(c => c.Format == "csv" && c.ContentLineCount >= 3);
        var json = s.Containers.ToList().FindIndex(c => c.Format == "json");
        Assert.True(s.Rename(csv, "Results table").Ok);
        Assert.True(s.ChangeFormat(json, "text").Ok);
        Assert.True(s.Merge(0).Ok);
        csv = s.Containers.ToList().FindIndex(c => c.Name == "Results table");
        var store = Store();
        var saved = s.SaveAsTemplate("i01 edited", store);
        Assert.True(saved.Result.Ok, saved.Result.Message);

        var lines = File.ReadAllText(IntakeTestData.PathOf("i01_sample")).Split('\n').ToList();
        var lastRow = (int)s.Containers[csv].ContentLastLine!.Value;
        lines.Insert(lastRow, lines[lastRow - 1]);
        var other = await SessionAsync(string.Join("\n", lines));
        var before = IntakePlanTests.Json(other.Containers);

        var result = other.ApplyTemplate(store.Get(saved.TemplateId!.Value)!);
        _output.WriteLine(result.Message);
        Assert.True(result.Ok, result.Message);
        Assert.Empty(result.Skipped);
        Assert.Equal(Shape(s), Shape(other));
        var applied = other.Containers.Single(c => c.Name == "Results table");
        Assert.Equal(s.Containers[csv].ContentLineCount + 1, applied.ContentLineCount);
        Assert.Equal(s.Containers[csv].ContentFirstLine, applied.ContentFirstLine);
        IntakeEditSessionTests.AssertEveryNonBlankLineInOneContainer(other, string.Join("\n", lines));

        Assert.Equal(1, other.UndoSteps);
        Assert.True(other.Undo().Ok);
        Assert.Equal(before, IntakePlanTests.Json(other.Containers));
        Assert.False(other.IsModified);
    }

    /// <summary>Tests (7), (1): the split anchor is found in a file with other line numbers and other values.</summary>
    [Fact]
    public async Task SplitAnchor_IsPlaced_WhereTheLineNumbersDiffer()
    {
        var s = await EditedReportAsync(4);
        var store = Store();
        var id = s.SaveAsTemplate("Lab report", store).TemplateId!.Value;
        var template = store.Get(id)!;
        Assert.True(template.Parts[^1].StartsInside);
        Assert.Equal("Signature section", template.Parts[^1].Anchor);

        var other = await SessionAsync(Report(57, seed: 3));
        Assert.Equal(TemplateMatch.Exact, other.SuggestTemplates(store).Single().Match);
        var result = other.ApplyTemplate(template);
        Assert.True(result.Ok, result.Message);
        Assert.Equal("Applied 'Lab report': 4 of 4 parts matched.", result.Message);
        Assert.Equal(Shape(s), Shape(other));
        var last = other.Containers[^1];
        Assert.Equal("Signature section", Lines(other, last)[0]);
        Assert.NotEqual(s.Containers[^1].ContentFirstLine, last.ContentFirstLine);
        Assert.All(other.Containers, c => Assert.True(c.ConfirmedByUser));
    }

    /// <summary>Test (8): an anchor that is not in the file is skipped and reported; its lines stay in the part before.</summary>
    [Fact]
    public async Task AnAnchorThatIsNotFound_IsSkipped_AndReported()
    {
        var s = await EditedReportAsync(4);
        var store = Store();
        var template = store.Get(s.SaveAsTemplate("Lab report", store).TemplateId!.Value)!;
        var other = await SessionAsync(Report(9, section: "Approval block"));
        var result = other.ApplyTemplate(template);
        Assert.True(result.Ok, result.Message);
        Assert.Equal("Applied 'Lab report': 3 of 4 parts matched, 1 skipped (no line 'Signature section' found).", result.Message);
        Assert.Equal(3, other.Containers.Count);
        Assert.Contains("Approval block", Lines(other, other.Containers[^1]));
    }

    /// <summary>Test (10): a template whose structure does not fit is refused and changes nothing.</summary>
    [Fact]
    public async Task ATemplateThatDoesNotMatch_IsRefused_AndChangesNothing()
    {
        var s = await EditedReportAsync(4);
        var store = Store();
        var template = store.Get(s.SaveAsTemplate("Lab report", store).TemplateId!.Value)!;
        var other = await SessionAsync("{\n  \"a\": 1\n}\n\n<root><x>1</x></root>\n");
        var before = IntakePlanTests.Json(other.Containers);
        var result = other.ApplyTemplate(template);
        Assert.False(result.Ok);
        Assert.Equal("The saved layout 'Lab report' does not fit this file, so nothing was changed.", result.Message);
        Assert.Equal(before, IntakePlanTests.Json(other.Containers));
        Assert.False(other.CanUndo);
    }

    /// <summary>Tests (2), (11): the template file holds no file name, folder, hash or size, so renaming or moving the file changes nothing.</summary>
    [Fact]
    public async Task TheTemplateFile_HasNoFileNameFolderHashOrSize()
    {
        var path = Path.Combine(_folder, "data", "instrument_run_42.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Report(6));
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(new FileIntakeFactory().CreateItem(path));
        var s = new IntakeEditSession(item, scan, null, "t");
        Assert.True(s.Rename(1, "Results").Ok);
        var store = Store();
        var id = s.SaveAsTemplate("Lab report", store).TemplateId!.Value;
        var text = File.ReadAllText(Path.Combine(_folder, "templates", id + ".json"));
        _output.WriteLine(text);
        Assert.DoesNotContain("instrument_run_42", text);
        Assert.DoesNotContain(_folder.Replace("\\", "\\\\"), text);
        Assert.DoesNotContain(s.Sha256, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scan.Intake!.File!.Size.ToString(), text);
        Assert.DoesNotContain("byte", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("line\"", text, StringComparison.OrdinalIgnoreCase);

        var moved = Path.Combine(_folder, "elsewhere", "renamed.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Copy(path, moved);
        var (movedItem, movedScan) = await IntakeEditSessionTests.ScanAsync(new FileIntakeFactory().CreateItem(moved));
        Assert.Equal(TemplateMatch.Exact, new IntakeEditSession(movedItem, movedScan, null, "m").SuggestTemplates(store).Single().Match);
    }

    /// <summary>Test (12): lineage lines for saved, suggested, applied and rejected, with template id and name, and no anchor text.</summary>
    [Fact]
    public async Task Lineage_SavedSuggestedAppliedRejected_HaveIdAndName_AndNoAnchorText()
    {
        var log = new TextFileLineageLog(new LineageSettings { Folder = Path.Combine(_folder, "lineage") });
        var s = await EditedReportAsync(4, log);
        var store = Store();
        var id = s.SaveAsTemplate("Lab report", store).TemplateId!.Value;
        var other = await SessionAsync(Report(20, seed: 2), log);
        other.SuggestTemplates(store);
        Assert.True(other.ApplyTemplate(store.Get(id)!).Ok);
        var json = await SessionAsync("{\n  \"a\": 1\n}\n", log);
        Assert.False(json.ApplyTemplate(store.Get(id)!).Ok);

        var text = File.ReadAllText(log.FilePath!);
        foreach (var name in new[] { "template.saved", "template.suggested", "template.applied", "template.rejected" })
        {
            var line = text.Split('\n').Single(l => l.Contains($"\"{name}\""));
            Assert.Contains(id.ToString("D"), line);
            Assert.Contains("Lab report", line);
        }

        Assert.DoesNotContain("Signature section", text);
        Assert.DoesNotContain("Col1", text);
    }

    /// <summary>Test (15): the original file's bytes are unchanged after save, suggest and apply.</summary>
    [Fact]
    public async Task TheOriginalFile_IsUnchanged()
    {
        var path = Path.Combine(_folder, "original.txt");
        File.WriteAllText(path, Report(12));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(new FileIntakeFactory().CreateItem(path));
        var s = new IntakeEditSession(item, scan, null, "o");
        var store = Store();
        var id = s.SaveAsTemplate("Lab", store).TemplateId!.Value;
        s.SuggestTemplates(store);
        Assert.True(s.ApplyTemplate(store.Get(id)!).Ok);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        File.Delete(path);
    }

    /// <summary>
    /// Test (13): which files match templates made from the other files (Auto-detect). Report only for
    /// the real files; Tier A (i08, i09, i10) must not match a different Tier A file unless the structures are equal.
    /// </summary>
    [Fact]
    public async Task MatchMatrix_ForTheReport_AndTierANeverMatchesAnotherStructure()
    {
        var names = Directory.GetFiles(IntakeTestData.Folder, "i*.txt").Select(Path.GetFileNameWithoutExtension).Select(n => (Name: n!, Path: IntakeTestData.PathOf(n!)))
            .Concat(Directory.GetFiles(Path.Combine(IntakeTestData.Folder, "real"), "r*.*").Where(p => !p.EndsWith(".md")).Select(p => (Name: Path.GetFileNameWithoutExtension(p)[..3], Path: p)))
            .OrderBy(n => n.Name).ToList();
        // Since G5c2 the templates are real saved templates (with column names), matched through the session.
        var store = Store();
        var sessions = new List<(string Name, IntakeEditSession Session, IntakeFingerprint Print)>();
        foreach (var (name, path) in names)
        {
            var (item, scan) = await IntakeEditSessionTests.ScanAsync(new FileIntakeFactory().CreateItem(path));
            var session = new IntakeEditSession(item, scan, null, name);
            Assert.True(session.SaveAsTemplate(name, store).Result.Ok);
            sessions.Add((name, session, session.AutomaticFingerprint()));
        }

        var all = new TemplateSettings { MaxCandidates = 100 };
        foreach (var (name, session, print) in sessions)
        {
            var found = session.SuggestTemplates(store, all).Where(c => c.Template.Name != name && c.Match != TemplateMatch.None)
                .Select(c => $"{c.Template.Name} {c.Match} {c.Similarity:0.00}{(c.NamesDiffer ? " (names differ)" : "")}");
            _output.WriteLine($"{name} ({print.Entries.Count} entries): {string.Join(", ", found.DefaultIfEmpty("none"))}");
        }

        var tierA = sessions.Where(p => p.Name.StartsWith("i08") || p.Name.StartsWith("i09") || p.Name.StartsWith("i10")).ToList();
        foreach (var a in tierA)
        {
            foreach (var b in tierA.Where(b => b.Name != a.Name && b.Print.Text != a.Print.Text))
            {
                var match = a.Session.SuggestTemplates(store, all).Single(c => c.Template.Name == b.Name);
                Assert.True(match.Match == TemplateMatch.None, $"{a.Name} matches {b.Name}: {match.Match} {match.Similarity}");
            }
        }

        var r09 = sessions.Single(x => x.Name == "r09").Session.SuggestTemplates(store, all).Single(c => c.Template.Name == "r12");
        Assert.NotEqual(TemplateMatch.Exact, r09.Match);
    }
}

/// <summary>Test (14): suggest and apply on 36,001 parts.</summary>
[Collection("Intake memory")]
public sealed class IntakeTemplateLargeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("g5c1_large_").FullName;

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    [Fact]
    public async Task SuggestAndApply_On36001Parts_AreFast_AndKeepTheG5aMemoryLimit()
    {
        var sample = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        var path = Path.Combine(_folder, "i01_x2000.txt");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            for (var i = 0; i < 2000; i++)
            {
                file.Write(sample);
            }
        }

        var store = new FileTemplateStore(new TemplateSettings { Folder = Path.Combine(_folder, "templates") }, Path.Combine(_folder, "fallback"));
        var small = await IntakeEditSessionTests.FileSessionAsync("i01_sample", IntakeMode.Markers);
        Assert.True(small.Rename(1, "First note").Ok);
        var id = small.SaveAsTemplate("i01 markers", store).TemplateId!.Value;
        var bigTemplate = store.Get(id)! with { Id = Guid.NewGuid(), Name = "repeated" };

        var item = new FileIntakeFactory().CreateItem(path);
        var intake = IntakeReader.Read(item, IntakeTestData.Markers, null, CancellationToken.None);
        var validation = new IntakeValidationResult(Guid.NewGuid(), intake.ItemId,
            intake.Containers.Select(c => new ContainerValidation(c.Order, ContainerStatus.Ok, [], 0, false)).ToList(), 0, 0, 0, 0, false, null);
        var scan = new IntakeScanResult(intake, validation, intake.EncodingUsed, false, 0, false, null, IntakeMode.Markers);
        var s = new IntakeEditSession(item, scan, null, "x");
        Assert.True(s.Containers.Count >= 36_000, $"{s.Containers.Count} containers");
        Assert.True(store.Save(bigTemplate with { Fingerprint = s.AutomaticFingerprint() }).Ok);

        var memory = LiveMemory.Bytes();
        var clock = Stopwatch.StartNew();
        var candidates = s.SuggestTemplates(store);
        var suggestMs = clock.ElapsedMilliseconds;
        var afterSuggest = LiveMemory.Bytes();
        clock.Restart();
        var result = s.ApplyTemplate(candidates[0].Template);
        var applyMs = clock.ElapsedMilliseconds;
        var afterApply = LiveMemory.Bytes();

        output.WriteLine($"containers: {s.Containers.Count}; fingerprint: {s.AutomaticFingerprint().Entries.Count} entries");
        output.WriteLine($"suggest: {suggestMs} ms, {afterSuggest - memory:N0} bytes; best {candidates[0].Template.Name} {candidates[0].Match} {candidates[0].Similarity}");
        output.WriteLine($"apply: {applyMs} ms, {afterApply - afterSuggest:N0} bytes; {result.Message}");
        Assert.True(result.Ok, result.Message);
        Assert.Equal(TemplateMatch.Exact, candidates[0].Match);
        Assert.True(suggestMs < 2000, $"suggest took {suggestMs} ms");
        Assert.True(applyMs < 2000, $"apply took {applyMs} ms");
        Assert.True(afterApply - memory < 300L * s.Containers.Count, $"suggest and apply added {afterApply - memory:N0} bytes");
    }
}
