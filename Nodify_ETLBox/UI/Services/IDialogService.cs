using EtlDesigner.Engine.Csv;
using EtlDesigner.UI.ViewModels;

namespace EtlDesigner.UI.Services;

/// <summary>The answer to "Save changes to the project?".</summary>
public enum UnsavedChangesChoice
{
    Save,
    DontSave,
    Cancel
}

public interface IDialogService
{
    void ShowCsvPreview(NodeViewModel node, CsvFileSettings settings);

    /// <summary>
    /// Opens the ParseValue dialog for <paramref name="node"/>, loads the upstream preview, and
    /// writes the settings back to the node on Save.
    /// </summary>
    void ShowParseValue(NodeViewModel node, ParseValueUpstream upstream);

    /// <summary>Opens the Execute dialog (modal, owned by the main window). Closing it cancels a running run.</summary>
    void ShowExecute(ExecuteViewModel viewModel);

    /// <summary>Asks for a CSV file, starting near <paramref name="currentPath"/>. Null when the user cancels.</summary>
    string? PickCsvFile(string? currentPath);

    /// <summary>Opens the Generate DLL dialog for CsvFile -> ParseValue -> DotNetObject (modal, owned by the main window).</summary>
    void ShowGenerateDll(GenerateDllViewModel viewModel);

    /// <summary>Asks for a folder, starting at <paramref name="currentFolder"/>. Null when the user cancels.</summary>
    string? PickFolder(string? currentFolder);

    /// <summary>Asks for a project file to open, starting near <paramref name="currentPath"/>. Null when the user cancels.</summary>
    string? PickProjectToOpen(string? currentPath);

    /// <summary>Asks where to save the project. The system dialog asks before it overwrites a file. Null when the user cancels.</summary>
    string? PickProjectToSave(string? currentPath, string projectName);

    /// <summary>Asks "Save changes to &lt;name&gt;?" with Save, Don't save and Cancel (modal, owned by the main window).</summary>
    UnsavedChangesChoice AskUnsavedChanges(string projectName);

    /// <summary>Shows a message that says what went wrong and what to do next.</summary>
    void ShowError(string title, string message);
}
