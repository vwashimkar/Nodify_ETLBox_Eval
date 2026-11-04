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

    /// <summary>Opens the Execute dialog (modal, owned by the main window). Closing it cancels a running run.</summary>
    void ShowExecute(ExecuteViewModel viewModel);

    /// <summary>Asks for a CSV file, starting near <paramref name="currentPath"/>. Null when the user cancels.</summary>
    string? PickCsvFile(string? currentPath);

    /// <summary>Opens the Generate DLL dialog for CsvFile -> ParseValue -> DotNetObject (modal, owned by the main window).</summary>
    void ShowGenerateDll(GenerateDllViewModel viewModel);

    /// <summary>Asks for a folder, starting at <paramref name="currentFolder"/>. Null when the user cancels.</summary>
    string? PickFolder(string? currentFolder);
}
