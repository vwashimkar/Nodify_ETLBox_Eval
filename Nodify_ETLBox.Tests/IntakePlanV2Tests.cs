using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;
using Xunit.Abstractions;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5c1 step 1: the compact saved layout (plan version 2).</summary>
public class IntakePlanV2Tests(ITestOutputHelper output)
{
    private static readonly string V1SamplePath = Path.Combine(AppContext.BaseDirectory, "TestData", "plan_v1_i01.json");

    private static string Json(IEnumerable<ContainerInfo> containers) => IntakePlanTests.Json(containers);

    private static string Checks(IEnumerable<ContainerValidation> validation)
        => string.Join("|", validation.Select(v => $"{v.Order}:{v.Status}:{v.TotalIssues}:{string.Join(",", v.Issues.Select(i => i.Message))}"));

    /// <summary>The three edits of the G5a test on i01 Auto-detect.</summary>
    private static async Task<IntakeEditSession> I01ThreeEditsAsync()
    {
        var s = await IntakeEditSessionTests.FileSessionAsync("i01_sample", IntakeMode.AutoDetect);
        var big = s.Containers.ToList().FindIndex(c => c.ContentLineCount >= 4);
        Assert.True(s.Split(big, s.Containers[big].ContentFirstLine!.Value + 2).Ok);
        Assert.True(s.Merge(0).Ok);
        Assert.True(s.Rename(1, "Second").Ok);
        return s;
    }

    /// <summary>Saves <paramref name="s"/>, opens the JSON on a fresh scan of the same item and asserts identical containers and checks.</summary>
    private static async Task<string> AssertRoundTripAsync(IntakeEditSession s)
    {
        var plan = IntakePlan.FromSession(s, s.Scan);
        Assert.Equal(IntakePlan.CurrentFormatVersion, plan.FormatVersion);
        var json = IntakePlanJson.Serialize(plan);
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(s.Item, s.Mode);
        var back = IntakePlanJson.Deserialize(json, out var error);
        Assert.True(back is not null, error);
        var fresh = IntakeEditSession.FromPlan(item, scan, back, null, "x", out var message);
        Assert.True(fresh is not null, message);
        Assert.Equal(Json(s.Containers), Json(fresh.Containers));
        Assert.Equal(Checks(s.Validation), Checks(fresh.Validation));
        return json;
    }

    [Fact]
    public async Task V2_RoundTrip_IsOneLine_WithoutNulls_AndGivesIdenticalContainersAndChecks()
    {
        var s = await I01ThreeEditsAsync();
        Assert.True(s.ChangeFormat(3, "text").Ok);
        Assert.True(s.SetTableOptions(s.Containers.ToList().FindIndex(c => c.Format == "csv"), skipLines: 1).Ok);
        var json = await AssertRoundTripAsync(s);

        Assert.StartsWith("{\"formatVersion\":2,", json);
        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain(":null", json);
        Assert.DoesNotContain("\"confirmedByUser\":false", json);
        Assert.DoesNotContain("startByte", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(json, IntakePlanJson.Serialize(IntakePlanJson.Deserialize(json, out _)!));
    }

    public static TheoryData<string> MarkerFiles() => [.. IntakeTestData.MarkerFiles];

    [Theory]
    [MemberData(nameof(MarkerFiles))]
    public async Task V2_MarkerFiles_SplitAndMergeAcrossMarkers_GiveIdenticalContainers(string name)
    {
        foreach (var mode in new[] { IntakeMode.Markers, IntakeMode.AutoDetect, IntakeMode.MarkersThenAutoDetect })
        {
            var s = await IntakeEditSessionTests.FileSessionAsync(name, mode);
            var big = s.Containers.ToList().FindIndex(c => c.ContentLineCount >= 3);
            if (big >= 0)
            {
                s.Split(big, s.Containers[big].ContentFirstLine!.Value + 1);
            }

            s.Merge(0);
            if (s.Containers.Count > 3)
            {
                s.Merge(s.Containers.Count - 3);
            }

            if (s.Containers.Count > 1)
            {
                s.Rename(1, "Renamed part");
            }

            await AssertRoundTripAsync(s);
        }
    }

    [Fact]
    public async Task V2_Utf16File_SplitAndMerge_GiveIdenticalContainers()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(IntakeEditSessionTests.Sample.Replace("\n", "\r\n"))).ToArray();
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.MemoryItem(bytes));
        var s = new IntakeEditSession(item, scan, null, "u");
        Assert.True(s.Split(0, 3).Ok);
        Assert.True(s.Merge(1).Ok);
        await AssertRoundTripAsync(s);
    }

    /// <summary>A plan saved by G5b (version 1) still loads and applies exactly as before.</summary>
    [Fact]
    public async Task V1SampleFile_StillLoads_AndApplies()
    {
        var edited = await I01ThreeEditsAsync();
        if (!File.Exists(V1SamplePath) && Environment.GetEnvironmentVariable("PLAN_V1_WRITE_SAMPLE") == "1")
        {
            var source = Path.Combine(Path.GetDirectoryName(typeof(IntakePlanV2Tests).Assembly.Location)!, "..", "..", "..", "TestData", "plan_v1_i01.json");
            File.WriteAllText(source, IntakePlanJson.Serialize(IntakePlanTests.V1(edited)));
            File.Copy(source, V1SamplePath);
        }

        var json = File.ReadAllText(V1SamplePath);
        Assert.Contains("\"formatVersion\": 1", json);
        var plan = IntakePlanJson.Deserialize(json, out var error);
        Assert.True(plan is not null, error);
        Assert.Equal(1, plan.FormatVersion);
        Assert.True(IntakePlan.TryApply(edited.Scan, plan, out var containers, out var message), message);
        Assert.Equal(Json(edited.Containers), Json(containers));

        var (item, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.Item("i01_sample"));
        var fresh = IntakeEditSession.FromPlan(item, scan, plan, null, "v1", out message);
        Assert.True(fresh is not null, message);
        Assert.Equal(Json(edited.Containers), Json(fresh.Containers));
    }

    [Fact]
    public async Task V2_OnAChangedFile_IsRefusedAsBefore()
    {
        var s = await IntakeEditSessionTests.SessionAsync(IntakeEditSessionTests.Sample);
        Assert.True(s.Split(0, 3).Ok);
        var plan = IntakePlan.FromSession(s, s.Scan);
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.MemoryItem(IntakeEditSessionTests.Sample.Replace("alpha", "alphA")));
        Assert.Null(IntakeEditSession.FromPlan(item, scan, plan, null, "c", out var message));
        Assert.Equal(IntakePlan.ChangedFileMessage, message);
    }

    [Fact]
    public async Task V2_WithoutTheFile_IsRefusedWithAPlainSentence()
    {
        var s = await IntakeEditSessionTests.SessionAsync(IntakeEditSessionTests.Sample);
        Assert.True(s.Split(0, 3).Ok);
        Assert.False(IntakePlan.TryApply(s.Scan, IntakePlan.FromSession(s, s.Scan), out var containers, out var message));
        Assert.Empty(containers);
        Assert.Equal("The saved layout could not be applied because the file is needed to rebuild it.", message);
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"sha256\":\"00\",\"fileSize\":1,\"mode\":\"AutoDetect\"")]
    [InlineData("{\"formatVersion\":2,\"sha256\":\"00\",\"fileSize\":1,\"mode\":\"AutoDetect\"}")]
    [InlineData("{\"formatVersion\":2,\"sha256\":\"00\",\"fileSize\":1,\"mode\":\"AutoDetect\",\"parts\":[{\"firstLine\":1,\"lastLine\":2,\"name\":\"x\"}]}")]
    [InlineData("{\"formatVersion\":2,\"sha256\":\"00\",\"fileSize\":1,\"mode\":\"Sideways\",\"parts\":[]}")]
    [InlineData("{\"formatVersion\":2,\"sha256\":\"00\",\"fileSize\":1,\"mode\":\"AutoDetect\",\"parts\":[{\"firstLine\":\"one\",\"format\":\"csv\",\"name\":\"x\"}]}")]
    public void V2_DamagedJson_IsRefusedWithThePlainSentence(string json)
    {
        Assert.Null(IntakePlanJson.Deserialize(json, out var error));
        Assert.Equal("The layout file could not be read because it is not a saved layout.", error);
    }

    [Fact]
    public async Task V2_ARangeThatDoesNotFit_IsRefused_AndNothingIsApplied()
    {
        var s = await IntakeEditSessionTests.SessionAsync(IntakeEditSessionTests.Sample);
        Assert.True(s.Split(0, 3).Ok);
        var plan = IntakePlan.FromSession(s, s.Scan);
        var parts = plan.Parts!.ToArray();
        parts[1] = parts[1] with { LastLine = 4 };
        Assert.False(IntakePlan.TryApply(s.Item, s.Scan, plan with { Parts = parts }, out var containers, out var message));
        Assert.Empty(containers);
        Assert.Equal(IntakePlan.DoesNotFitMessage, message);

        parts = plan.Parts!.ToArray();
        parts[^1] = parts[^1] with { LastLine = 99 };
        Assert.False(IntakePlan.TryApply(s.Item, s.Scan, plan with { Parts = parts }, out _, out message));
        Assert.Equal(IntakePlan.DoesNotFitMessage, message);
    }

    /// <summary>The size target: at most 150 characters per part on average (i01 repeated 50 times, both modes).</summary>
    [Theory]
    [InlineData(IntakeMode.Markers)]
    [InlineData(IntakeMode.AutoDetect)]
    public async Task V2_Size_IsAtMost150CharactersPerPart(IntakeMode mode)
    {
        var one = File.ReadAllBytes(IntakeTestData.PathOf("i01_sample"));
        var bytes = Enumerable.Repeat(one, 50).SelectMany(b => b).ToArray();
        var (item, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.MemoryItem(bytes), mode);
        var s = new IntakeEditSession(item, scan, null, "size");
        Assert.True(s.Rename(0, "First part").Ok);
        var plan = IntakePlan.FromSession(s, s.Scan);
        var json = IntakePlanJson.Serialize(plan);
        var perPart = (double)json.Length / s.Containers.Count;
        output.WriteLine($"i01 x50 {mode}: {s.Containers.Count} parts, {json.Length:N0} characters, {perPart:N1} per part");
        Assert.Equal(2, plan.FormatVersion);
        Assert.True(perPart <= 150, $"{perPart:N1} characters per part");
    }
}
