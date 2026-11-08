using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G4a step 2: the labels file. Never touches the real AppData folder.</summary>
public class LabelsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "labels-" + Guid.NewGuid().ToString("N"));

    public LabelsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => ProjectEditorTests.DeleteTempFolder(_folder);

    private string Write(string text)
    {
        var path = Path.Combine(_folder, "labels.txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void GoodLines_AreRead_WithSpacesTrimmed()
    {
        var labels = Labels.Parse("node.fileintake.title = Instrument file\n  container.format.csv=Table  \nmy_key-2 = x");

        Assert.Equal((3, 0), (labels.Count, labels.IgnoredLines));
        Assert.Equal("Instrument file", labels.Get("node.fileintake.title", "fallback"));
        Assert.Equal("Table", labels.Find("container.format.csv"));
        Assert.Equal("fallback", labels.Get("no.such.key", "fallback"));
    }

    [Theory]
    [InlineData("Node.Title = upper case key")]
    [InlineData("node title = space in key")]
    [InlineData("no equals sign here")]
    [InlineData("node.title =   ")]
    [InlineData("= no key")]
    [InlineData("node.ä = not ascii")]
    public void BadLines_AreIgnored_AndCounted(string line)
    {
        var labels = Labels.Parse("good.key = ok\n" + line);

        Assert.Equal((1, 1), (labels.Count, labels.IgnoredLines));
    }

    [Fact]
    public void Comments_AndBlankLines_AreNotCounted()
    {
        var labels = Labels.Parse("# a comment = with equals\n\n   # indented comment\r\nkey = value\r\n");

        Assert.Equal((1, 0, "value"), (labels.Count, labels.IgnoredLines, labels.Find("key")));
    }

    [Fact]
    public void TheLastDuplicate_Wins()
    {
        Assert.Equal("second", Labels.Parse("key = first\nkey = second").Find("key"));
    }

    [Fact]
    public void ALongLine_OrALongValue_IsIgnored()
    {
        var labels = Labels.Parse($"long.value = {new string('v', 81)}\nok.value = {new string('v', 80)}\nlong.line = x{new string(' ', 200)}");

        Assert.Equal((1, 2), (labels.Count, labels.IgnoredLines));
        Assert.NotNull(labels.Find("ok.value"));
    }

    [Fact]
    public void UnicodeValues_AreKept()
    {
        var labels = Labels.Load(Write("container.format.csv = Tabelle (µg/mL, °C) – 表\n"));

        Assert.Equal("Tabelle (µg/mL, °C) – 表", labels.Find("container.format.csv"));
        Assert.Null(labels.LoadNote);
    }

    [Fact]
    public void AFileOver64Kb_IsNotRead()
    {
        var labels = Labels.Load(Write("a.key = value\n" + new string('#', 64 * 1024)));

        Assert.Equal(0, labels.Count);
        Assert.Contains("64 KB", labels.LoadNote);
    }

    [Fact]
    public void AMissingFile_GivesEmptyLabels_AndANote()
    {
        var labels = Labels.Load(Path.Combine(_folder, "missing", "labels.txt"));

        Assert.Equal(0, labels.Count);
        Assert.NotNull(labels.LoadNote);
        Assert.DoesNotContain(_folder, labels.LoadNote);
    }

    [Fact]
    public void ALockedFile_GivesEmptyLabels_AndANote()
    {
        var path = Write("a.key = value\n");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var labels = Labels.Load(path);

            Assert.Equal(0, labels.Count);
            Assert.Contains("could not be read", labels.LoadNote);
            Assert.DoesNotContain(_folder, labels.LoadNote);
        }
    }

    [Fact]
    public void TheDefaultFile_ListsEveryKey_AndReadsBackToTheDefaults()
    {
        var path = LabelsLocation.DefaultPath(_folder);

        Assert.True(Labels.WriteDefaultFile(path));
        var text = File.ReadAllText(path);
        foreach (var key in LabelsSettings.DefaultValues.Keys)
        {
            Assert.Contains($"# {key} = ", text);
        }

        var asWritten = Labels.Load(path);
        Assert.Equal((0, 0, null), (asWritten.Count, asWritten.IgnoredLines, asWritten.LoadNote));
        Assert.All(LabelsSettings.DefaultValues, p => Assert.Equal(p.Value, asWritten.Get(p.Key, p.Value)));

        // With every '#' in front of a key removed, the file gives exactly the defaults.
        var uncommented = Labels.Parse(string.Join("\n", text.Split('\n').Select(l => l.StartsWith("# container.", StringComparison.Ordinal) || l.StartsWith("# node.", StringComparison.Ordinal) ? l[2..] : l)));
        Assert.Equal(LabelsSettings.DefaultValues.Count, uncommented.Count);
        Assert.All(LabelsSettings.DefaultValues, p => Assert.Equal(p.Value, uncommented.Find(p.Key)));
    }

    [Fact]
    public void TheDefaultFile_NeverOverwrites()
    {
        var path = Write("mine = kept\n");

        Assert.False(Labels.WriteDefaultFile(path));
        Assert.Equal("mine = kept\n", File.ReadAllText(path));
    }

    [Fact]
    public void DefaultPath_IsUnderTheBaseFolder()
    {
        Assert.Equal(Path.Combine(_folder, "EtlDesigner", "labels.txt"), LabelsLocation.DefaultPath(_folder));
        Assert.EndsWith(Path.Combine("EtlDesigner", "labels.txt"), LabelsLocation.DefaultPath());
    }

    [Fact]
    public void KnownKeys_IncludeTheNodeAndEveryFormatName()
    {
        Assert.Contains("node.fileintake.title", LabelsSettings.DefaultValues.Keys);
        Assert.Contains("node.fileintake.subtitle", LabelsSettings.DefaultValues.Keys);
        Assert.Equal("XML (SOAP)", LabelsSettings.DefaultValues["container.format.xml.soap"]);
        Assert.Equal("Fixed-width table", LabelsSettings.DefaultValues["container.format.fixedwidth"]);
    }

    [Fact]
    public void ThroughTheConfigStore_TheTextIsParsed()
    {
        var store = new MemoryStore(new() { ["labels"] = "container.format.csv = Table" });

        Assert.Equal("Table", Labels.Load(store, "labels").Find("container.format.csv"));
        Assert.NotNull(Labels.Load(store, "other").LoadNote);
    }

    private sealed class MemoryStore(Dictionary<string, string> texts) : IConfigStore
    {
        public string? ReadText(string key) => texts.GetValueOrDefault(key);

        public void WriteText(string key, string text) => texts[key] = text;
    }

    // ---- ContainerDisplay with labels -----------------------------------------------------

    private static ContainerInfo Info(string format, string? subType = null, string? version = null)
        => new(1, ContainerKind.Detected, format, "x", "", null, null, 1, 1, 1, 0, 1, ContainerStatus.Ok, "", ContainerConfidence.Sure, subType, SubTypeVersion: version);

    [Fact]
    public void Format_UsesAnOverride_ByFormatOrBySubType()
    {
        var labels = Labels.Parse("container.format.csv = Table\ncontainer.format.xml.soap = SOAP message\ncontainer.format.json = JSON data");

        Assert.Equal("Table", ContainerDisplay.Format(Info("csv"), labels));
        Assert.Equal("SOAP message", ContainerDisplay.Format(Info("xml", "soap", "1.2"), labels));
        Assert.Equal("SOAP message", ContainerDisplay.Format(Info("soap"), labels));
        Assert.Equal("JSON data", ContainerDisplay.Format(Info("json"), labels));
        Assert.Equal("JSON (BadgerFish)", ContainerDisplay.Format(Info("json", "badgerfish"), labels));
        Assert.Equal("TSV table", ContainerDisplay.Format(Info("tsv"), labels));
    }
}
