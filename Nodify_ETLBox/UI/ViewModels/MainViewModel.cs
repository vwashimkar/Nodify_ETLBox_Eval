using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class MainViewModel : ObservableObject
{
    private string _filteredJsonOutput;
    private string _parsedJsonOutput;

    public MainViewModel()
    {
        Editor = new EditorViewModel();
        _filteredJsonOutput = "";
        _parsedJsonOutput = "";
    }

    public EditorViewModel Editor { get; }

    public string FilteredJsonOutput
    {
        get => _filteredJsonOutput;
        set => SetProperty(ref _filteredJsonOutput, value ?? "");
    }

    public string ParsedJsonOutput
    {
        get => _parsedJsonOutput;
        set => SetProperty(ref _parsedJsonOutput, value ?? "");
    }
}
