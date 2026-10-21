using Microsoft.Win32;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public enum SettingKind
{
    Text,
    FilePath,
    ComboBox,
    Fields
}

public class SettingViewModel : ObservableObject
{
    private string _value;

    public SettingViewModel(string key, string label, SettingKind kind, bool required, string defaultValue = "")
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Kind = kind;
        Required = required;
        _value = defaultValue ?? "";

        BrowseCommand = new RelayCommand(_ => Browse());
    }

    public string Key { get; }

    public string Label { get; }

    public SettingKind Kind { get; }

    public bool Required { get; }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? "");
    }

    /// <summary>
    /// True when this is a required field and Value is empty.
    /// </summary>
    public bool IsMissing => Required && string.IsNullOrEmpty(Value);

    /// <summary>
    /// For ComboBox settings only: the list of selectable items.
    /// </summary>
    public List<string> ComboBoxItems { get; } = new();

    public RelayCommand BrowseCommand { get; }

    private void Browse()
    {
        if (Kind != SettingKind.FilePath)
            return;

        var dialog = new OpenFileDialog
        {
            Title = $"Select file for {Label}",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            Value = dialog.FileName;
        }
    }

    public override string ToString() => $"{Label}: {Value}";
}
