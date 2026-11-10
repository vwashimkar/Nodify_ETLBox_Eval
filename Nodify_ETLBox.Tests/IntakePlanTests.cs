using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>Task G5a step 2: the saved plan of a session and applying it to a scan.</summary>
public class IntakePlanTests
{
    private static async Task<(IntakeEditSession Session, IntakePlan Plan)> EditedSampleAsync()
    {
        var s = await IntakeEditSessionTests.SessionAsync(IntakeEditSessionTests.Sample);
        Assert.True(s.Split(0, 3).Ok);
        Assert.True(s.Rename(0, "Top rows").Ok);
        Assert.True(s.ChangeFormat(2, "keyvalue").Ok);
        return (s, V1(s));
    }

    /// <summary>The session as a version 1 plan, every field of every container, as G5a wrote it (FromSession writes version 2 since G5c1).</summary>
    internal static IntakePlan V1(IntakeEditSession s)
    {
        var file = s.Scan.Intake!.File!;
        return new IntakePlan(IntakePlan.FullFormatVersion, file.Sha256, file.Size, s.Scan.Intake.EncodingUsed ?? file.Encoding, s.Scan.Mode,
            s.Containers.Select(c => new PlanContainer(
                c.ContentFirstLine, c.ContentLastLine, c.Format, c.SubType, c.Delimiter, c.HeaderGuess, c.SkipLines, c.KeyValueForm, c.Name, c.ConfirmedByUser,
                c.Kind, c.MarkerBeginLine, c.MarkerEndLine, c.ContentStartByte, c.ContentEndByte, c.Confidence, c.SubTypeVersion, c.Notes, c.Status, c.Message,
                c.Reasons)).ToList());
    }

    /// <summary>Containers as JSON, so lists such as Reasons compare by content.</summary>
    internal static string Json(IEnumerable<ContainerInfo> containers) => System.Text.Json.JsonSerializer.Serialize(containers.ToList());

    private static PlanContainer[] Copy(IntakePlan plan) => [.. plan.Containers];

    private static void AssertRefused(IntakeScanResult scan, IntakePlan plan, string expected)
    {
        Assert.False(IntakePlan.TryApply(scan, plan, out var containers, out var message));
        Assert.Empty(containers);
        Assert.Contains(expected, message);
    }

    [Fact]
    public async Task Json_RoundTrip_GivesTheSamePlan()
    {
        var (_, plan) = await EditedSampleAsync();
        var json = IntakePlanJson.Serialize(plan);
        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"mode\": \"AutoDetect\"", json);
        Assert.Contains("\"format\": \"keyvalue\"", json);

        var back = IntakePlanJson.Deserialize(json, out var error);
        Assert.Null(error);
        Assert.Equal(IntakePlanJson.Serialize(plan), IntakePlanJson.Serialize(back!));
    }

    [Fact]
    public async Task Json_UnknownProperties_AreIgnored_AndAFutureVersionIsRefused()
    {
        var (_, plan) = await EditedSampleAsync();
        var json = IntakePlanJson.Serialize(plan);
        Assert.NotNull(IntakePlanJson.Deserialize(json.Replace("\"formatVersion\": 1,", "\"formatVersion\": 1, \"comment\": \"later\","), out _));

        Assert.Null(IntakePlanJson.Deserialize(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 3"), out var error));
        Assert.Equal("This layout was saved by a newer version of the tool; update the tool to use it.", error);

        Assert.Null(IntakePlanJson.Deserialize("{ not json", out error));
        Assert.Contains("not a saved layout", error);
    }

    [Fact]
    public async Task Apply_ToTheSameFile_GivesTheSessionsContainers_AndFromPlanStartsThere()
    {
        var (s, plan) = await EditedSampleAsync();
        Assert.True(IntakePlan.TryApply(s.Scan, plan, out var containers, out var message), message);
        Assert.Equal(s.Containers, containers);

        var fresh = IntakeEditSession.FromPlan(s.Item, s.Scan, plan, null, "x", out message);
        Assert.NotNull(fresh);
        Assert.Equal(s.Containers, fresh.Containers);
        Assert.Equal(s.Validation.Select(v => (v.Order, v.Status, v.TotalIssues)), fresh.Validation.Select(v => (v.Order, v.Status, v.TotalIssues)));
        Assert.True(fresh.IsModified);
        Assert.True(fresh.Revert().Ok);
        Assert.Equal(s.Scan.Intake!.Containers, fresh.Containers);
    }

    [Fact]
    public async Task Apply_AfterOneByteChanged_IsRefused()
    {
        var (_, plan) = await EditedSampleAsync();
        var changed = IntakeEditSessionTests.Sample.Replace("alpha", "alphA");
        var (_, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.MemoryItem(changed));
        AssertRefused(scan, plan, IntakePlan.ChangedFileMessage);
    }

    [Fact]
    public async Task Apply_WithAnotherSize_IsRefused()
    {
        var (s, plan) = await EditedSampleAsync();
        AssertRefused(s.Scan, plan with { FileSize = plan.FileSize + 1 }, IntakePlan.ChangedFileMessage);
    }

    [Fact]
    public async Task Apply_OverlappingRanges_AreRefused()
    {
        var (s, plan) = await EditedSampleAsync();
        var containers = Copy(plan);
        containers[1] = containers[1] with { FirstLine = containers[0].LastLine, StartByte = containers[0].EndByte };
        AssertRefused(s.Scan, plan with { Containers = containers }, IntakePlan.DoesNotFitMessage);
    }

    [Fact]
    public async Task Apply_AGapThatHidesANonBlankLine_IsRefused()
    {
        var (s, plan) = await EditedSampleAsync();
        var containers = Copy(plan);
        // Container 2 holds lines 3-5; line 5 is not blank.
        Assert.Equal(5, containers[1].LastLine);
        containers[1] = containers[1] with { LastLine = 4, EndByte = containers[1].EndByte - 11 };
        AssertRefused(s.Scan, plan with { Containers = containers }, IntakePlan.DoesNotFitMessage);
    }

    [Fact]
    public async Task Apply_ARangeOutsideTheFile_IsRefused()
    {
        var (s, plan) = await EditedSampleAsync();
        var containers = Copy(plan);
        containers[^1] = containers[^1] with { LastLine = 99 };
        AssertRefused(s.Scan, plan with { Containers = containers }, IntakePlan.DoesNotFitMessage);
    }

    [Theory]
    [InlineData("excel", null, 0, "unknown format 'excel'")]
    [InlineData("csv", "colon", 0, "delimiter its format does not allow")]
    [InlineData("csv", ",", 9, "more skip lines than lines")]
    public async Task Apply_FormatsAndOptions_PassTheEditChecks(string format, string? delimiter, int skip, string expected)
    {
        var (s, plan) = await EditedSampleAsync();
        var containers = Copy(plan);
        containers[0] = containers[0] with { Format = format, Delimiter = delimiter, SkipLines = skip };
        AssertRefused(s.Scan, plan with { Containers = containers }, expected);
    }

    [Fact]
    public async Task Apply_AFutureFormatVersion_IsRefused()
    {
        var (s, plan) = await EditedSampleAsync();
        AssertRefused(s.Scan, plan with { FormatVersion = 3 }, "newer version");
    }

    [Fact]
    public async Task I01_PlanAfterThreeEdits_OnAFreshScan_GivesIdenticalContainers()
    {
        var s = await IntakeEditSessionTests.FileSessionAsync("i01_sample", IntakeMode.AutoDetect);
        var big = s.Containers.ToList().FindIndex(c => c.ContentLineCount >= 4);
        Assert.True(s.Split(big, s.Containers[big].ContentFirstLine!.Value + 2).Ok);
        Assert.True(s.Merge(0).Ok);
        Assert.True(s.Rename(1, "Second").Ok);
        var plan = IntakePlan.FromSession(s, s.Scan);
        Assert.Equal(2, plan.FormatVersion);
        var json = IntakePlanJson.Serialize(plan);

        var (item, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.Item("i01_sample"));
        var fresh = IntakeEditSession.FromPlan(item, scan, IntakePlanJson.Deserialize(json, out _)!, null, "y", out var message);
        Assert.True(fresh is not null, message);
        Assert.Equal(Json(s.Containers), Json(fresh.Containers));
    }

    [Fact]
    public async Task I01_PlanSavedInMarkersMode_IsRefusedForAnAutoScan()
    {
        var s = await IntakeEditSessionTests.FileSessionAsync("i01_sample", IntakeMode.Markers);
        var plan = IntakePlan.FromSession(s, s.Scan);
        var (_, scan) = await IntakeEditSessionTests.ScanAsync(IntakeTestData.Item("i01_sample"));
        AssertRefused(scan, plan, "another reading mode");
    }
}
