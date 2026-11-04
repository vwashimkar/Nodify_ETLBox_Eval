using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Services;

public interface IDialogService
{
    void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings);

    /// <summary>
    /// Opens the ParseValue dialog for <paramref name="node"/>, loads the upstream preview, and
    /// writes the settings back to the node on Save.
    /// </summary>
    void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream);
}
