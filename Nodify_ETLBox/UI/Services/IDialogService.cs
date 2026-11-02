using EtlDesigner.Engine.Csv;

namespace EtlDesigner.UI.Services;

public interface IDialogService
{
    void ShowCsvPreview(CsvFileSettings settings);
}