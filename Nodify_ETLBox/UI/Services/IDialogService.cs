using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Services;

public interface IDialogService
{
    void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings);
}
