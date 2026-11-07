using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class DetectorModelTests
{
    [Fact]
    public void DetectorSettings_HaveThePlanDefaults()
    {
        var detector = new IntakeSettings().Detector;

        Assert.Equal(3, detector.MinTableLines);
        Assert.Equal((2, 3), (detector.MinShortTableLines, detector.MinShortTableFields));
        Assert.Equal(new[] { ",", ";", "|", "\t" }, detector.DelimiterCandidates);
        Assert.Equal(3, detector.MinKeyValueLines);
        Assert.Equal(2, detector.TextSplitBlankLines);
        Assert.Equal(1, detector.FieldCountTolerance);
        Assert.Equal(64, detector.MaxValidationMb);
    }

    [Fact]
    public void DetectorSettings_RoundTripThroughJson()
    {
        var settings = new IntakeSettings
        {
            Detector = new DetectorSettings { MinTableLines = 4, DelimiterCandidates = [";", "\t"], TextSplitBlankLines = 3, MaxValidationMb = 8 }
        };

        var json = IntakeJson.Serialize(settings);
        var back = IntakeJson.DeserializeSettings(json);

        Assert.Contains("\"detector\": {", json);
        Assert.Equal(json, IntakeJson.Serialize(back));
        Assert.Equal(new[] { ";", "\t" }, back.Detector.DelimiterCandidates);
        Assert.Equal(4, back.Detector.MinTableLines);
    }

    [Fact]
    public void OldSettingsJson_WithoutADetector_GetsTheDefaults()
    {
        var back = IntakeJson.DeserializeSettings("{ \"mode\": \"Markers\" }");

        Assert.Equal(new DetectorSettings().MinTableLines, back.Detector.MinTableLines);
    }

    [Fact]
    public void NewContainerFields_RoundTripThroughJson_AndOldJsonStillReads()
    {
        var table = new ContainerInfo(1, ContainerKind.Detected, "csv", "Monthly Sales", "", null, null, 3, 9, 7, 10, 200,
            ContainerStatus.Ok, "", ContainerConfidence.Likely, null, ";", HeaderGuess.Yes, 2, null, ["5 lines, 3 fields", "header guessed"]);
        var keyValue = new ContainerInfo(2, ContainerKind.Detected, "keyvalue", "keyvalue-1", "", null, null, 11, 13, 3, 210, 260,
            ContainerStatus.Ok, "", ContainerConfidence.Guessed, KeyValueForm: KeyValueForm.EqualsSign);
        var result = new IntakeResult(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, "1", null, [table, keyValue], [], 5, false, null);

        var json = IntakeJson.Serialize(result);
        var back = IntakeJson.DeserializeResult(json);

        Assert.Equal(json, IntakeJson.Serialize(back));
        Assert.Contains("\"headerGuess\": \"Yes\"", json);
        Assert.Contains("\"keyValueForm\": \"EqualsSign\"", json);
        Assert.Equal(new[] { "5 lines, 3 fields", "header guessed" }, back.Containers[0].Reasons);
        Assert.Equal(table with { Reasons = null }, back.Containers[0] with { Reasons = null });

        var old = json.Replace("\"subType\": null,", "").Replace("\"skipLines\": 2,", "");
        Assert.Equal(0, IntakeJson.DeserializeResult(old).Containers[0].SkipLines);
    }
}
