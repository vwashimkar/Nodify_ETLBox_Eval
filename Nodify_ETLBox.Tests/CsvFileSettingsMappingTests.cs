using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

public class CsvFileSettingsMappingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadPreviewCommand_BuildsCsvFileSettings_WithHeaderRowCheckboxValue(bool headerRow)
    {
        CsvFileSettings? captured = null;

        var node = new NodeViewModel(Guid.NewGuid(), "CsvFile", NodeType.CsvFile, settings =>
        {
            captured = settings;
            return true;
        });

        node.Settings.Add(new SettingViewModel("FilePath", "File path", SettingKind.FilePath, required: true, "dummy.csv"));
        node.Settings.Add(new SettingViewModel("Separator", "Separator", SettingKind.ComboBox, required: true, "Comma"));

        var hasHeader = new SettingViewModel("HasHeader", "Header row", SettingKind.CheckBox, required: false, bool.TrueString)
        {
            BoolValue = headerRow
        };
        node.Settings.Add(hasHeader);

        node.Settings.Add(new SettingViewModel("SkipLines", "Skip lines", SettingKind.Number, required: true, "0"));
        node.Settings.Add(new SettingViewModel("EncodingName", "Encoding", SettingKind.ComboBox, required: true, "utf-8"));
        node.Settings.Add(new SettingViewModel("PreviewRows", "Preview rows", SettingKind.Number, required: true, "5"));

        node.LoadPreviewCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(headerRow, captured!.HasHeader);
    }
}
