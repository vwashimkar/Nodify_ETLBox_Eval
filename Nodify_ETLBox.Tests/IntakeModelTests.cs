using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class IntakeModelTests
{
    [Fact]
    public void Settings_HaveThePlanDefaults()
    {
        var settings = new IntakeSettings();

        Assert.Equal(IntakeMode.AutoDetect, settings.Mode);
        Assert.Equal(500, settings.MaxFileSizeMb);
        Assert.Equal("auto", settings.Encoding);
        Assert.Equal("##### BEGIN ", settings.Markers.BeginPrefix);
        Assert.Equal(" #####", settings.Markers.BeginSuffix);
        Assert.Equal("##### END #####", settings.Markers.EndLine);
        Assert.Equal('|', settings.Markers.FieldSeparator);
    }

    [Fact]
    public void Settings_RoundTrip_WithEnumsAsStrings()
    {
        var settings = new IntakeSettings
        {
            Mode = IntakeMode.MarkersThenAutoDetect,
            MaxFileSizeMb = 12,
            Encoding = "windows-1252",
            Markers = new MarkerProfile { BeginPrefix = "== ", BeginSuffix = " ==", EndLine = "-- end --", FieldSeparator = ';', KnownFormats = ["csv"] }
        };

        var json = IntakeJson.Serialize(settings);
        var back = IntakeJson.DeserializeSettings(json);

        Assert.Contains("\"mode\": \"MarkersThenAutoDetect\"", json);
        Assert.Equal(json, IntakeJson.Serialize(back));
        Assert.Equal(settings.Mode, back.Mode);
        Assert.Equal(12, back.MaxFileSizeMb);
        Assert.Equal(["csv"], back.Markers.KnownFormats);
    }

    [Fact]
    public void Settings_MissingFields_GetTheDefaults()
    {
        var settings = IntakeJson.DeserializeSettings("{\"mode\": \"Markers\"}");

        Assert.Equal(IntakeMode.Markers, settings.Mode);
        Assert.Equal(500, settings.MaxFileSizeMb);
        Assert.Equal(MarkerProfile.DefaultKnownFormats, settings.Markers.KnownFormats);
    }

    [Fact]
    public void Result_RoundTrip_WithEnumsAsStrings()
    {
        var result = IntakeTestData.ReadMarkers("i06_stray_end");

        var json = IntakeJson.Serialize(result);
        var back = IntakeJson.DeserializeResult(json);

        Assert.Contains("\"kind\": \"GapText\"", json);
        Assert.Contains("\"status\": \"Ok\"", json);
        Assert.Contains("\"confidence\": \"Sure\"", json);
        Assert.Contains("\"lineEnding\": \"Lf\"", json);
        Assert.Equal(json, IntakeJson.Serialize(back));
        Assert.Equal(result.Containers, back.Containers);
        Assert.Equal(result.Warnings, back.Warnings);
        Assert.Equal(result.File, back.File);
        Assert.Equal(result.RunId, back.RunId);
        Assert.Equal(result.StartedUtc, back.StartedUtc);
        Assert.Equal(DateTimeKind.Utc, back.StartedUtc.Kind);
    }

    [Fact]
    public void Result_HasRunFields()
    {
        var item = IntakeTestData.Item("i01_sample");

        var first = IntakeTestData.ReadMarkers(item);
        var second = IntakeTestData.ReadMarkers(item);

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(item.Id, first.ItemId);
        Assert.False(string.IsNullOrEmpty(first.ToolVersion));
        Assert.True(first.FinishedUtc >= first.StartedUtc);
        Assert.Equal(DateTimeKind.Utc, first.StartedUtc.Kind);
    }

    [Fact]
    public void FileItem_DescribesTheFile()
    {
        var path = IntakeTestData.PathOf("i01_sample");

        var item = new FileIntakeFactory().CreateItem(path);

        Assert.Equal("file", item.SourceKind);
        Assert.Equal("i01_sample.txt", item.Name);
        Assert.Equal(6487, item.Size);
        Assert.NotEqual(Guid.Empty, item.Id);
        using var stream = item.OpenRead();
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void Registry_FindsTheFileFactory()
    {
        var factory = IntakeFactoryRegistry.Default.Find("file", out var error);

        Assert.IsType<FileIntakeFactory>(factory);
        Assert.Null(error);
    }

    [Fact]
    public void Registry_UnknownKind_GivesAClearMessage()
    {
        var factory = IntakeFactoryRegistry.Default.Find("mqtt", out var error);

        Assert.Null(factory);
        Assert.Equal("Source kind 'mqtt' is not known. Use one of: file.", error);
    }
}
