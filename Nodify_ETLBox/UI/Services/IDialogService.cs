using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Services;

public interface IDialogService
{
    void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings);

    /// <summary>Opens the ParseValue dialog (a read-only placeholder for now).</summary>
    void ShowParseValue(NodeViewModel node);
}
