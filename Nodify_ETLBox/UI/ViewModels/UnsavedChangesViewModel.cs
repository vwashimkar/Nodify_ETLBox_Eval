using EtlDesigner.UI.Mvvm;
using EtlDesigner.UI.Services;

namespace EtlDesigner.UI.ViewModels;

/// <summary>The unsaved-changes question. Closing the dialog any other way counts as Cancel.</summary>
public sealed class UnsavedChangesViewModel
{
    public UnsavedChangesViewModel(string projectName)
    {
        Message = $"Save changes to {projectName}?";
        SaveCommand = new RelayCommand(_ => Choose(UnsavedChangesChoice.Save));
        DontSaveCommand = new RelayCommand(_ => Choose(UnsavedChangesChoice.DontSave));
        CancelCommand = new RelayCommand(_ => Choose(UnsavedChangesChoice.Cancel));
    }

    public string Message { get; }

    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    public RelayCommand SaveCommand { get; }

    public RelayCommand DontSaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public event EventHandler? CloseRequested;

    private void Choose(UnsavedChangesChoice choice)
    {
        Choice = choice;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
