using System.IO;
using System.Windows;
using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class DataStudioViewModel : ObservableObject
{
    public DataStudioViewModel(CsvFileSettings settings)
    {
        Settings = settings;
        FileName = Path.GetFileName(settings.Path);

        SettingsSummary =
            $"Path: {settings.Path}{Environment.NewLine}" +
            $"Separator: {settings.Separator}{Environment.NewLine}" +
            $"HasHeader: {settings.HasHeader}{Environment.NewLine}" +
            $"SkipLines: {settings.SkipLines}{Environment.NewLine}" +
            $"EncodingName: {settings.EncodingName}{Environment.NewLine}" +
            $"PreviewRows: {settings.PreviewRows}";

        CloseCommand = new RelayCommand<Window>(window => window?.Close());
    }

    public CsvFileSettings Settings { get; }

    public string FileName { get; }

    public string SettingsSummary { get; }

    public RelayCommand<Window> CloseCommand { get; }
}