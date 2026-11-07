using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>One rule of the auto-detector per test, on tiny files in memory.</summary>
public class AutoDetectorRuleTests
{
    private static IntakeResult Detect(string text)
        => IntakeReader.Read(IntakeTestData.MemoryItem(text), new IntakeSettings(), null, CancellationToken.None);

    private static List<(string Format, long First, long Last)> Shape(string text)
        => Detect(text).Containers.Select(c => (c.Format, c.ContentFirstLine!.Value, c.ContentLastLine!.Value)).ToList();

    [Fact]
    public void Json_WithABlankLineInside_IsOneBlock()
    {
        var result = Detect("intro line\n\n{\n  \"a\": 1,\n\n  \"b\": [1, 2]\n}\n");

        Assert.Equal([("text", 1L, 1L), ("json", 3L, 7L)], result.Containers.Select(c => (c.Format, c.ContentFirstLine!.Value, c.ContentLastLine!.Value)));
        Assert.Equal(ContainerConfidence.Sure, result.Containers[1].Confidence);
    }

    [Fact]
    public void Json_WithBracesInsideStrings_EndsAtTheRealClose()
    {
        Assert.Equal([("json", 1L, 4L), ("text", 6L, 6L)], Shape("{\n  \"a\": \"} not the end {\",\n  \"b\": \"]\"\n}\n\nafter\n"));
    }

    [Fact]
    public void JsonThatDoesNotParse_IsText_WithAReason()
    {
        var container = Assert.Single(Detect("{\n  \"a\": 1,,\n}\n").Containers);

        Assert.Equal("text", container.Format);
        Assert.Contains(container.Reasons!, r => r.Contains("does not parse"));
    }

    [Fact]
    public void JsonLines_AreOneBlock_AndOneJsonLineAloneIsJson()
    {
        Assert.Equal([("json-lines", 1L, 3L)], Shape("{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n"));
        Assert.Equal([("json", 1L, 1L)], Shape("{\"a\":1}\n"));
    }

    [Fact]
    public void Xml_WithACommentAndCData_IsOneBlock()
    {
        const string xml = "<?xml version=\"1.0\"?>\n<root>\n  <!-- a <comment> -->\n  <a><![CDATA[ </root> ]]></a>\n</root>\n";

        var container = Assert.Single(Detect(xml).Containers);
        Assert.Equal(("xml", 1L, 5L, ContainerConfidence.Sure), (container.Format, container.ContentFirstLine!.Value, container.ContentLastLine!.Value, container.Confidence));
        Assert.Null(container.SubType);
    }

    [Fact]
    public void Soap_IsXmlWithTheSubTypeSoap()
    {
        const string soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">\n  <s:Body/>\n</s:Envelope>\n";

        var container = Assert.Single(Detect(soap).Containers);
        Assert.Equal(("xml", "soap"), (container.Format, container.SubType));
    }

    [Fact]
    public void Xml_WithADoctype_IsRefusedSafely_WithAWarning()
    {
        const string xml = "<?xml version=\"1.0\"?>\n<!DOCTYPE r [ <!ENTITY e SYSTEM \"http://127.0.0.1:9/never\"> ]>\n<r>&e;</r>\n";

        var result = Detect(xml);

        var container = Assert.Single(result.Containers);
        Assert.Equal(("text", ContainerStatus.Warning), (container.Format, container.Status));
        Assert.Contains("DOCTYPE", Assert.Single(result.Warnings).Message);
    }

    [Fact]
    public void TwoTables_WithTheSameLayout_SplitByOneBlankLine_AreTwoContainers()
    {
        Assert.Equal([("csv", 1L, 3L), ("csv", 5L, 7L)], Shape("a,b,c\n1,2,3\n4,5,6\n\n7,8,9\n1,2,3\n4,5,6\n"));
    }

    [Fact]
    public void Semicolons_BeatDecimalCommas()
    {
        var container = Assert.Single(Detect("produkt;menge;preis\nApfel;10;1,25\nBirne;4;2,10\nKirsche;100;0,05\n").Containers);

        Assert.Equal(("csv", ";", HeaderGuess.Yes), (container.Format, container.Delimiter, container.HeaderGuess));
    }

    [Fact]
    public void AQuotedFieldSpanningTwoLines_StaysInOneRow()
    {
        var container = Assert.Single(Detect("id,who,text\nT-1,asha,\"one\ntwo\"\nT-2,ravi,\"three\"\n").Containers);

        Assert.Equal(("csv", 1L, 4L), (container.Format, container.ContentFirstLine!.Value, container.ContentLastLine!.Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TitleLinesAboveATable_AreSkipLines_AndTheFirstNamesIt(int titles)
    {
        var text = string.Concat(Enumerable.Range(1, titles).Select(i => $"Report part {i}:\n")) + "x,y,z\n1,2,3\n4,5,6\n";

        var container = Assert.Single(Detect(text).Containers);

        Assert.Equal(("csv", titles, 1L), (container.Format, container.SkipLines, container.ContentFirstLine!.Value));
        Assert.Equal("Report part 1", container.Name);
    }

    [Fact]
    public void FourTitleLines_GiveThreeSkipLines_AndTheFirstStaysText()
    {
        var result = Detect("Title 1\nTitle 2\nTitle 3\nTitle 4\nx,y,z\n1,2,3\n4,5,6\n");

        Assert.Equal([("text", 1L, 1L), ("csv", 2L, 7L)], result.Containers.Select(c => (c.Format, c.ContentFirstLine!.Value, c.ContentLastLine!.Value)));
        Assert.Equal(3, result.Containers[1].SkipLines);
    }

    [Fact]
    public void AWeakLine_InsideATable_StaysInIt_AtItsEdge_IsNotPartOfIt()
    {
        Assert.Equal([("csv", 1L, 5L)], Shape("a,b,c\n1,2,3\n,,,\n4,5,6\n7,8,9\n"));
        Assert.Equal([("csv", 1L, 3L), ("text", 4L, 4L)], Shape("a,b,c\n1,2,3\n4,5,6\n,,\n"));
        Assert.Equal([("text", 1L, 1L), ("csv", 2L, 4L)], Shape("\"\"\na,b,c\n1,2,3\n4,5,6\n"));
    }

    [Theory]
    [InlineData("Name: one\nPlace: two\nDate: 2026-11-06\n", KeyValueForm.Colon)]
    [InlineData("* Name = one\n* Place = two\n* Date = 2026-11-06\n", KeyValueForm.EqualsSign)]
    [InlineData("\"Name\",\"one\"\n\"Place\",\"two\",\"three\"\n\"Date\",\"2026-11-06\"\n", KeyValueForm.Quoted)]
    [InlineData("Name\tone\nPlace\ttwo\tthree\nDate\t2026-11-06\n", KeyValueForm.Tab)]
    [InlineData("Name: one, Place: two\nDate: 2026, Time: 9, Zone: x\nUser: a, Role: b\n", KeyValueForm.Pairs)]
    public void KeyValue_InEachForm(string text, KeyValueForm form)
    {
        var container = Assert.Single(Detect(text).Containers);

        Assert.Equal(("keyvalue", form, 1L, 3L), (container.Format, container.KeyValueForm!.Value, container.ContentFirstLine!.Value, container.ContentLastLine!.Value));
    }

    [Fact]
    public void ProseThatLooksLikeKeyValue_IsKeyValue_ButOnlyGuessed()
    {
        var container = Assert.Single(Detect("Note: we met today.\nTopic: the parser.\nOutcome: more tests.\n").Containers);

        Assert.Equal(("keyvalue", ContainerConfidence.Guessed), (container.Format, container.Confidence));
    }

    [Fact]
    public void OneBlankLine_DoesNotSplitText_TwoDo()
    {
        Assert.Equal([("text", 1L, 3L)], Shape("first words here.\n\nmore words here.\n"));
        Assert.Equal([("text", 1L, 1L), ("text", 4L, 4L)], Shape("first words here.\n\n\nmore words here.\n"));
    }

    [Fact]
    public void AFileOfBlankLines_HasNoContainers_AndNoError()
    {
        var result = Detect("\n  \n\t\n\n");

        Assert.Null(result.Error);
        Assert.Empty(result.Containers);
    }

    [Fact]
    public void AOneLineFile_IsOneTextContainer()
    {
        var container = Assert.Single(Detect("just one line").Containers);

        Assert.Equal(("text", 1L, 1L, ContainerConfidence.Guessed), (container.Format, container.ContentFirstLine!.Value, container.ContentLastLine!.Value, container.Confidence));
    }

    [Fact]
    public void ATableAfterAnotherTable_WithNoBlankLine_IsSplitAndGuessed()
    {
        var result = Detect("a,b,c\n1,2,3\n4,5,6\nw,x,y,z,v\n1,2,3,4,5\n6,7,8,9,0\n");

        Assert.Equal([("csv", 1L, 3L), ("csv", 4L, 6L)], result.Containers.Select(c => (c.Format, c.ContentFirstLine!.Value, c.ContentLastLine!.Value)));
        Assert.Equal(ContainerConfidence.Guessed, result.Containers[1].Confidence);
    }

    [Fact]
    public void AHeaderWithOneFieldMore_IsTolerated_AndLikely()
    {
        var container = Assert.Single(Detect("a,b,c,d\n1,2,3\n4,5,6\n7,8,9\n8,9,0\n").Containers);

        Assert.Equal(("csv", 0, ContainerConfidence.Likely), (container.Format, container.SkipLines, container.Confidence));
    }

    // ---- G2b step 1: trailing empty fields ----------------------------------------------------

    [Fact]
    public void ATrailingDelimiterInTheHeader_IsNotAField_SoTheTableIsExact()
    {
        var container = Assert.Single(Detect("a,b,c,\n1,2,3\n4,5,6\n7,8,9\n8,9,0\n").Containers);

        Assert.Equal(("csv", 0, ContainerConfidence.Sure), (container.Format, container.SkipLines, container.Confidence));
    }

    [Fact]
    public void TrailingEmptyFields_DoNotCount_SoALineOfTwoFieldsPaddedWithCommas_HasTwo()
    {
        var fields = RowShape.Summary(RowShape.Split("a,b,,,,", ','));

        Assert.Equal((2, 6), (fields.Count, fields.RawCount));
        Assert.Equal([("csv", 1L, 4L)], Shape("x,y,,,\n1,2,,,\n3,4\n5,6,,\n"));
    }

    [Fact]
    public void ALineOfOnlyDelimitersOrEmptyQuotes_StaysWeak_InsideATable()
    {
        Assert.True(RowShape.IsWeak(",,,,", [',']));
        Assert.True(RowShape.IsWeak("\"\",\"\"", [',']));
        Assert.Equal([("csv", 1L, 5L)], Shape("a,b,c\n1,2,3\n,,,\n\"\",\"\"\n4,5,6\n"));
    }

    [Fact]
    public void KeyValuePairs_PaddedWithCommasToATableWidth_AreKeyValuePairs()
    {
        var container = Assert.Single(Detect("Name: one, Place: two,,,,,\nDate: x, Time: y,,,,,\nUser: a, Role: b,,,,,\n").Containers);

        Assert.Equal(("keyvalue", KeyValueForm.Pairs, 1L, 3L), (container.Format, container.KeyValueForm!.Value, container.ContentFirstLine!.Value, container.ContentLastLine!.Value));
    }

    [Fact]
    public void PairLines_AndColonLines_WithAWeakLineBetween_AreOneKeyValueBlock()
    {
        Assert.Equal([("keyvalue", 1L, 4L), ("text", 5L, 5L)], Shape("Name: one, Place: two,,,\nDate: x, Time: y,,,\n,,,,\nOperator: z,,,\nSome closing words,,,\n"));
    }

    // ---- G2b step 2: ragged right -------------------------------------------------------------

    [Fact]
    public void ATenFieldHeader_WithRowsOfTwoAndThreeFields_IsOneRaggedTable_Likely()
    {
        var container = Assert.Single(Detect("h1,h2,h3,h4,h5,h6,h7,h8,h9,h10\nA,1\nB,2,3\nC,4\nD,5,6\n").Containers);

        Assert.Equal(("csv", 1L, 5L, ContainerConfidence.Likely), (container.Format, container.ContentFirstLine!.Value, container.ContentLastLine!.Value, container.Confidence));
        Assert.Contains("rows shorter than header", container.Reasons!.First(r => r.Contains("shorter")));
    }

    [Fact]
    public void ABlock_WhereEveryLineHasADifferentFieldCount_IsNotATable()
    {
        Assert.DoesNotContain(Detect("a,b,c,d,e,f\n1,2,3,4,5\n1,2,3,4\n1,2,3\n1,2\n").Containers, c => c.Format == "csv");
    }

    [Fact]
    public void ARowWithOneField_EndsARaggedTable()
    {
        Assert.Equal([("csv", 1L, 4L), ("text", 5L, 5L)], Shape("a,b,c,d,e\n1,2\n3,4\n5,6,7\nclosing words\n"));
    }

    [Fact]
    public void AShorterRowWithNoNumber_UnderATableWithAHeader_StartsTheNextTable()
    {
        Assert.Equal([("csv", 1L, 3L), ("csv", 4L, 5L)], Shape("id,name,size,mass,flag\n1,a,2,3,4\n2,b,5,6,7\nid,name,size\n3,c,8\n"));
    }

    // ---- G2b step 3: key-value against a table ------------------------------------------------

    [Theory]
    [InlineData("Name\tAlpha\nPlace\tLab 2\nDate\t2026-11-06\n", KeyValueForm.Tab)]
    [InlineData("Temperature,25.3,C\nOperator,ann,-\nDate,2026-11-06,-\nPlate,P1,-\n", KeyValueForm.Delimited)]
    public void DistinctLabels_InTheFirstColumn_AndNoHeader_AreKeyValue_Likely(string text, KeyValueForm form)
    {
        var container = Assert.Single(Detect(text).Containers);

        Assert.Equal(("keyvalue", form, ContainerConfidence.Likely), (container.Format, container.KeyValueForm!.Value, container.Confidence));
        Assert.Contains("distinct labels in first column, no header", container.Reasons!);
    }

    [Theory]
    [InlineData("name,value\nalpha,1\nbeta,2\ngamma,3\n")]
    [InlineData("A,x\nA,y\nB,z\n")]
    [InlineData("1,x\n2,y\n3,z\n")]
    [InlineData("A,1\nB,2\nC,3\n")]
    [InlineData("ticket,owner,text\nT-1,ann,first words\nT-2,bob,more words\n")]
    public void AHeader_ARepeatedOrNumericFirstColumn_OrOnlyNumbers_KeepATable(string text)
    {
        Assert.Equal("csv", Assert.Single(Detect(text).Containers).Format);
    }

    public static TheoryData<string> I10Variants => new() { "utf-16le", "utf-16be", "crlf" };

    [Theory]
    [MemberData(nameof(I10Variants))]
    public void I10_AsUtf16OrCrLf_GivesTheSameContainers(string variant)
    {
        var text = File.ReadAllText(IntakeTestData.PathOf("i10_instrument_header_table_footer"));
        var bytes = variant switch
        {
            "utf-16le" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            "utf-16be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
            _ => Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n"))
        };

        static List<ContainerInfo> WithoutOffsets(IntakeResult r) => r.Containers.Select(c => c with { ContentStartByte = null, ContentEndByte = null }).ToList();
        var original = WithoutOffsets(AutoDetectTruthTests.Detect(IntakeTestData.PathOf("i10_instrument_header_table_footer")));
        var other = WithoutOffsets(IntakeReader.Read(IntakeTestData.MemoryItem(bytes), new IntakeSettings(), null, CancellationToken.None));

        Assert.Equal(original.Select(c => (c.Format, c.Name, c.ContentFirstLine, c.ContentLastLine, c.Confidence, c.SkipLines)),
            other.Select(c => (c.Format, c.Name, c.ContentFirstLine, c.ContentLastLine, c.Confidence, c.SkipLines)));
    }

    [Fact]
    public void MarkersThenAutoDetect_DetectsOnlyTheGaps_WithLineNumbersOfTheWholeFile()
    {
        const string text = "a,b,c\n1,2,3\n4,5,6\n##### BEGIN json | j | n #####\n{\"x\":1}\n##### END #####\nName: one\nPlace: two\nDate: three\n";

        var result = IntakeReader.Read(IntakeTestData.MemoryItem(text), new IntakeSettings { Mode = IntakeMode.MarkersThenAutoDetect }, null, CancellationToken.None);

        Assert.Equal([(ContainerKind.Detected, "csv", 1L), (ContainerKind.Marker, "json", 5L), (ContainerKind.Detected, "keyvalue", 7L)],
            result.Containers.Select(c => (c.Kind, c.Format, c.ContentFirstLine!.Value)));
    }
}
