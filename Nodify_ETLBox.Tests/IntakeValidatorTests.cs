using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The validator on small containers written here: one rule per test, line numbers in the whole file.</summary>
public class IntakeValidatorTests
{
    /// <summary>One container over lines <paramref name="first"/> to <paramref name="last"/> of an LF text, with the offsets the reader would give.</summary>
    internal static (IntakeItem Item, IntakeResult Result) One(string text, string format, long first, long last, string? delimiter = null,
        HeaderGuess? header = null, int skip = 0, KeyValueForm? form = null)
    {
        var lines = text.Split('\n');
        long start = 0;
        for (var i = 0; i < first - 1; i++)
        {
            start += Encoding.UTF8.GetByteCount(lines[i]) + 1;
        }

        var end = start;
        for (var i = first - 1; i < last; i++)
        {
            end += Encoding.UTF8.GetByteCount(lines[i]) + (i < last - 1 ? 1 : 0);
        }

        var container = new ContainerInfo(1, ContainerKind.Detected, format, format + "-1", "", null, null, first, last, last - first + 1, start, end,
            ContainerStatus.Ok, "", ContainerConfidence.Sure, null, delimiter, header, skip, form);
        var item = IntakeTestData.MemoryItem(text);
        return (item, new IntakeResult(Guid.NewGuid(), item.Id, DateTime.UtcNow, DateTime.UtcNow, "test", null, [container], [], 0, false, null));
    }

    internal static ContainerValidation Check(string text, string format, long first = 1, long? last = null, string? delimiter = null, HeaderGuess? header = null,
        int skip = 0, KeyValueForm? form = null, IntakeSettings? settings = null)
    {
        var (item, result) = One(text, format, first, last ?? text.TrimEnd('\n').Split('\n').Length, delimiter, header, skip, form);
        var validation = IntakeValidator.Validate(item, result, null, CancellationToken.None, settings);
        Assert.Null(validation.Error);
        var container = Assert.Single(validation.Containers);
        AssertPlain(container);
        return container;
    }

    /// <summary>No message names a .NET exception, holds a stack trace or a full path.</summary>
    internal static void AssertPlain(ContainerValidation container)
    {
        foreach (var issue in container.Issues)
        {
            Assert.DoesNotContain("Exception", issue.Message);
            Assert.DoesNotContain("   at ", issue.Message);
            Assert.DoesNotMatch(@"[A-Za-z]:\\", issue.Message);
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        }
    }

    private static ValidationIssue Only(ContainerValidation container, string code) => Assert.Single(container.Issues, i => i.Code == code);

    // ---- JSON -------------------------------------------------------------------------------

    [Fact]
    public void Json_Valid_IsOk()
    {
        var container = Check("{\n  \"a\": 1,\n  \"b\": [1, 2]\n}", "json");

        Assert.Equal((ContainerStatus.Ok, 0), (container.Status, container.TotalIssues));
    }

    [Fact]
    public void Json_MissingComma_InAContainerAtLine40_GivesTheFileLine42()
    {
        var text = string.Concat(Enumerable.Repeat("filler line\n", 39)) + "{\n  \"a\": 1\n  \"b\": 2\n}";

        var container = Check(text, "json", first: 40);

        var issue = Only(container, "json.syntax");
        Assert.Equal((ContainerStatus.Error, ValidationSeverity.Error, 42L), (container.Status, issue.Severity, issue.Line!.Value));
        Assert.NotNull(issue.Column);
    }

    [Fact]
    public void Json_DuplicatePropertyName_IsAWarning_WithItsLine()
    {
        var container = Check("{\n  \"a\": 1,\n  \"a\": 2\n}", "json");

        Assert.Equal((ContainerStatus.Warning, 3L), (container.Status, Only(container, "json.duplicate-key").Line!.Value));
    }

    [Fact]
    public void Json_Nested200Deep_IsAnError_WithoutAStackOverflow()
    {
        var container = Check(new string('[', 200) + new string(']', 200), "json");

        Assert.Equal(ContainerStatus.Error, container.Status);
        Only(container, "json.depth");
    }

    [Fact]
    public void JsonLines_ListsEachBadLine()
    {
        var container = Check("{\"a\":1}\n[1,2]\n{\"a\":\n{\"b\":2}\n{\"c\":3} {\"d\":4}", "json-lines");

        Assert.Equal([2L, 3L, 5L], container.Issues.Where(i => i.Code == "jsonl.line").Select(i => i.Line!.Value));
        Assert.Equal(ContainerStatus.Error, container.Status);
    }

    // ---- XML and SOAP -----------------------------------------------------------------------

    [Fact]
    public void Xml_MismatchedTag_IsAnError_WithTheFileLine()
    {
        var container = Check("intro\n<a>\n  <b>text</c>\n</a>", "xml", first: 2);

        var issue = Only(container, "xml.syntax");
        Assert.Equal(3L, issue.Line!.Value);
    }

    [Fact]
    public void Xml_WithADoctype_IsAnError_AndNotParsed()
    {
        var container = Check("<?xml version=\"1.0\"?>\n<!DOCTYPE r [ <!ENTITY e SYSTEM \"http://127.0.0.1:9/never\"> ]>\n<r>&e;</r>", "xml");

        var issue = Only(container, "xml.doctype");
        Assert.Equal((1, 2L), (container.TotalIssues, issue.Line!.Value));
    }

    [Theory]
    [InlineData("http://schemas.xmlsoap.org/soap/envelope/", "<faultcode>s:Server</faultcode><faultstring>Plate reader offline</faultstring>")]
    [InlineData("http://www.w3.org/2003/05/soap-envelope", "<s:Code><s:Value>s:Receiver</s:Value></s:Code><s:Reason><s:Text xml:lang=\"en\">Plate reader offline</s:Text></s:Reason>")]
    public void SoapFault_IsAWarning_WithTheFaultText(string ns, string fault)
    {
        var xml = $"<s:Envelope xmlns:s=\"{ns}\">\n  <s:Body>\n    <s:Fault>{fault}</s:Fault>\n  </s:Body>\n</s:Envelope>";

        var container = Check(xml, "xml");

        var issue = Only(container, "soap.fault");
        Assert.Equal(("SOAP fault: Plate reader offline", 3L, ContainerStatus.Warning), (issue.Message, issue.Line!.Value, container.Status));
    }

    [Fact]
    public void SoapFaultText_IsCutTo200Characters()
    {
        var xml = $"<s:Envelope xmlns:s=\"{SoapNamespaces.V11}\"><s:Body><s:Fault><faultstring>{new string('x', 500)}</faultstring></s:Fault></s:Body></s:Envelope>";

        Assert.True(Only(Check(xml, "xml"), "soap.fault").Message.Length <= "SOAP fault: ".Length + 203);
    }

    // ---- csv and tsv ------------------------------------------------------------------------

    [Fact]
    public void Csv_ARaggedRow_IsListedWithItsLine()
    {
        var container = Check("id,name,size\n1,a,2\n2,b\n3,c,4", "csv", delimiter: ",", header: HeaderGuess.Yes);

        var issue = Only(container, "csv.field-count");
        Assert.Equal((3L, ContainerStatus.Warning), (issue.Line!.Value, container.Status));
    }

    [Fact]
    public void Csv_AnUnterminatedQuote_IsAnError_AtTheLineWhereItStarts()
    {
        var container = Check("id,text\n1,\"open\n2,more\n3,end", "csv", delimiter: ",", header: HeaderGuess.Yes);

        Assert.Equal((2L, ContainerStatus.Error), (Only(container, "csv.unterminated-quote").Line!.Value, container.Status));
    }

    [Fact]
    public void Csv_DuplicateAndEmptyHeaderNames_AreWarnings()
    {
        var container = Check("id,name,,name\n1,a,x,b\n2,c,y,d", "csv", delimiter: ",", header: HeaderGuess.Yes);

        Assert.Contains("'name'", Only(container, "csv.header-duplicate").Message);
        Assert.Contains("3", Only(container, "csv.header-empty").Message);
    }

    [Fact]
    public void Csv_TrailingEmptyFields_AndAQuotedFieldOverTwoLines_AreFine()
    {
        var container = Check("a\tb\tc\n1\t\"x\ny\"\t3\n4\t5\t", "tsv", delimiter: "\t", header: HeaderGuess.Yes);

        Assert.Equal(ContainerStatus.Ok, container.Status);
    }

    [Fact]
    public void Csv_WithNoDataRows_IsAWarning_AndSkipLinesAreSkipped()
    {
        var container = Check("Title line\nid,name", "csv", delimiter: ",", header: HeaderGuess.Yes, skip: 1);

        Only(container, "csv.no-rows");
    }

    [Fact]
    public void SkipLines_AboveJson_AreNotParsed()
    {
        Assert.Equal(ContainerStatus.Ok, Check("====Data====\n{\n  \"a\": 1\n}", "json", skip: 1).Status);
    }

    [Theory]
    [InlineData("produkt;menge;preis\nApfel;10;1,25\nBirne;4;2,10")]
    [InlineData("id|name|size\n1|a|2\n2|b|3")]
    public void Csv_WithoutADelimiter_GuessesIt(string text)
    {
        Assert.Equal(ContainerStatus.Ok, Check(text, "csv").Status);
    }

    // ---- key-value --------------------------------------------------------------------------

    [Fact]
    public void KeyValue_DuplicateKey_IsAWarning_WithBothLines()
    {
        var container = Check("Name: one\nPlace: two\nName: three\nDate:", "keyvalue", form: KeyValueForm.Colon);

        var duplicate = Only(container, "kv.duplicate-key");
        Assert.Equal(3L, duplicate.Line!.Value);
        Assert.Contains("line 1", duplicate.Message);
        Assert.Equal(4L, Only(container, "kv.empty-value").Line!.Value);
        Assert.Equal(ContainerStatus.Warning, container.Status);
    }

    [Fact]
    public void KeyValue_ALineOfAnotherForm_IsAWarning()
    {
        var container = Check("Name: one\nPlace: two\njust a sentence here\nDate: today", "keyvalue", form: KeyValueForm.Colon);

        Assert.Equal(3L, Only(container, "kv.line-form").Line!.Value);
    }

    [Theory]
    [InlineData("Name\tone\nPlace\ttwo\nName\tthree", KeyValueForm.Tab, "\t")]
    [InlineData("* Name = one\n* Place = two\n* Name = three", KeyValueForm.EqualsSign, null)]
    [InlineData("Temp,25,C\nUser,ann,-\nTemp,26,C", KeyValueForm.Delimited, ",")]
    public void KeyValue_DuplicateKeys_InOtherForms(string text, KeyValueForm form, string? delimiter)
    {
        Assert.Equal(3L, Only(Check(text, "keyvalue", form: form, delimiter: delimiter), "kv.duplicate-key").Line!.Value);
    }

    // ---- YAML ------------------------------------------------------------------------------

    [Fact]
    public void Yaml_Valid_IsOk()
    {
        Assert.Equal(ContainerStatus.Ok, Check("defaults: &d\n  retries: 3\nservice:\n  <<: *d\n  list:\n    - a\n    - b\n  text: |\n    kept\n", "yaml").Status);
    }

    [Fact]
    public void Yaml_ABadIndent_IsAnError_WithTheFileLine()
    {
        var container = Check("before\nservice:\n  name: x\n port: 1\n", "yaml", first: 2, last: 4);

        var issue = Only(container, "yaml.syntax");
        Assert.InRange(issue.Line!.Value, 3L, 4L);
        Assert.Equal(ContainerStatus.Error, container.Status);
    }

    [Fact]
    public async Task Yaml_AHugeAliasFanOut_StopsAtTheLimit_Quickly()
    {
        var text = new StringBuilder("a: &a [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 0; i < 40; i++)
        {
            text.Append($"k{i}: [").Append(string.Join(", ", Enumerable.Repeat("*a", 100))).Append("]\n");
        }

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var container = await TestTimeout.Run(() => Task.FromResult(Check(text.ToString(), "yaml")));
        var grown = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.Contains("alias", Only(container, "yaml.limit").Message);
        Assert.True(grown < 16 * 1024 * 1024, $"memory grew by {grown:N0} bytes");
    }

    [Fact]
    public void Yaml_TooDeep_StopsAtTheDepthLimit()
    {
        Assert.Contains("deeper", Only(Check(new string('[', 100) + new string(']', 100), "yaml"), "yaml.limit").Message);
    }

    // ---- every format -------------------------------------------------------------------

    [Theory]
    [InlineData("text")]
    [InlineData("csv")]
    public void AReplacementCharacterOrNul_IsAWarning_WithTheFirstLine(string format)
    {
        var container = Check("a,b\n1,2\n3,�\n4,\0", format, delimiter: ",", header: HeaderGuess.Yes);

        var issue = Only(container, "encoding.bad-characters");
        Assert.Equal(3L, issue.Line!.Value);
        Assert.StartsWith("2 characters", issue.Message);
    }

    [Theory]
    [InlineData("fixedwidth")]
    [InlineData("someformat")]
    public void AFormatWithNoChecks_IsNotChecked_AndHasNoIssue(string format)
    {
        var container = Check("A 1 2 3\nB 4 5 6", format);

        Assert.Equal((ContainerStatus.Ok, 0, false), (container.Status, container.TotalIssues, container.Checked));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public void AFormatWithChecks_IsChecked(string format)
    {
        Assert.True(Check("{}", format).Checked);
    }

    [Fact]
    public void IssuesAreCappedAt20_AndTheRestIsCounted()
    {
        // 25 single bad rows, each followed by a good row, so none are grouped.
        var text = "a,b,c\n" + string.Concat(Enumerable.Range(1, 25).Select(i => $"{i},x\n1,2,3\n")) + "1,2,3";

        var container = Check(text, "csv", delimiter: ",", header: HeaderGuess.Yes);

        Assert.Equal((20, 25, true), (container.Issues.Count, container.TotalIssues, container.MoreNotShown));
    }

    [Fact]
    public void AContainerLargerThanTheLimit_IsCheckedOnItsFirstPartOnly()
    {
        var text = "a,b,c\n" + string.Concat(Enumerable.Repeat("1234567890,2234567890,3234567890\n", 70_000)) + "1,2,3";
        var settings = new IntakeSettings { Validation = new ValidationSettings { MaxValidationMb = 1 } };

        var container = Check(text, "csv", delimiter: ",", header: HeaderGuess.Yes, settings: settings);

        Assert.Equal((ContainerStatus.Warning, 1), (container.Status, container.TotalIssues));
        Assert.Contains("first 1 MB", Only(container, "validate.partial").Message);
    }

    [Fact]
    public void AMissingFile_IsAnErrorInTheResult_NotAnException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"intake-missing-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "a,b,c\n1,2,3\n4,5,6\n");
        var item = new FileIntakeFactory().CreateItem(path);
        var result = IntakeReader.Read(item, new IntakeSettings(), null, CancellationToken.None);
        File.Delete(path);

        var validation = IntakeValidator.Validate(item, result, null, CancellationToken.None);

        Assert.NotNull(validation.Error);
        Assert.DoesNotContain(path, validation.Error);
    }

    // ---- model ----------------------------------------------------------------------------

    [Fact]
    public void ValidationResult_RoundTripsThroughJson_WithEnumsAsStrings()
    {
        var issue = new ValidationIssue(ValidationSeverity.Warning, "csv.field-count", "Rows 12 to 16 have 2 fields, but the header has 3.", 12, null, 5);
        var result = new IntakeValidationResult(Guid.NewGuid(), Guid.NewGuid(),
            [new ContainerValidation(1, ContainerStatus.Warning, [issue], 25, true), new ContainerValidation(2, ContainerStatus.Ok, [], 0, false, Checked: false)],
            0, 25, 0, 7, false, null);

        var json = IntakeJson.Serialize(result);
        var back = IntakeJson.DeserializeValidation(json);

        Assert.Equal(json, IntakeJson.Serialize(back));
        Assert.Contains("\"severity\": \"Warning\"", json);
        Assert.Contains("\"status\": \"Warning\"", json);
        Assert.Equal(issue, back.Containers[0].Issues[0]);
        Assert.Equal(5L, back.Containers[0].Issues[0].LineCount);
        Assert.Equal((true, false), (back.Containers[0].Checked, back.Containers[1].Checked));
    }

    [Fact]
    public void ValidationSettings_HaveTheirDefaults_AndRoundTrip()
    {
        var settings = new IntakeSettings { Validation = new ValidationSettings { MaxIssuesPerContainer = 5, YamlMaxAliases = 10 } };

        var back = IntakeJson.DeserializeSettings(IntakeJson.Serialize(settings));

        Assert.Equal((5, 10, 64, 16), (back.Validation.MaxIssuesPerContainer, back.Validation.YamlMaxAliases, back.Validation.JsonMaxDepth, back.Validation.MaxValidationMb));
        Assert.Equal(20, new ValidationSettings().MaxIssuesPerContainer);
    }
}
