using System.Globalization;
using System.IO;
using EtlDesigner.Engine.Csv;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>What the CSV part settings dialog asks the host to do after it closed.</summary>
public enum CsvPartSettingsNext
{
    None,
    OpenIntakeStudio,
    ChooseColumns
}

/// <summary>
/// The read-only CSV part settings dialog (G6d): what the node's selector expects and where the part was found.
/// The selector is never edited here; "Open in Intake Studio" closes the dialog and opens the Studio of the
/// connected File intake node, "Choose columns" closes it and opens Data Studio on the part. No window types.
/// </summary>
public sealed class CsvPartSettingsViewModel : ObservableObject
{
    public const string NoFileIntakeText = "Connect a File intake node to this node first.";
    public const string PartNotReadText = "The part is not read yet. Wait until the node says Ready, or fix the line in the Issues tab.";
    public const string NotSet = "(not set)";

    public CsvPartSettingsViewModel(NodeViewModel node, string? filePath, bool hasFileIntake, CsvPartRead? read)
    {
        ArgumentNullException.ThrowIfNull(node);
        Title = $"{node.Title} settings";
        Status = node.CsvPartStatus ?? string.Empty;
        ExampleFile = string.IsNullOrWhiteSpace(filePath) ? NotSet : Path.GetFileName(filePath.Trim());

        var selector = node.CsvPartSelector;
        var location = read?.Result.Location;
        if (selector is null)
        {
            TemplateName = PartText = Delimiter = Header = SkipLines = Columns = ColumnNames = ExampleSha256 = NotSet;
            KeptText = NotSet;
        }
        else
        {
            var expected = selector.Expected;
            TemplateName = selector.Template.Name;
            PartText = location is { Ok: true, PartNumber: { } number, FirstLine: { } first, LastLine: { } last }
                ? $"Part {N(number)}, lines {N(first)} to {N(last)}"
                : $"Part {N(selector.PartIndex + 1)}, lines {N(selector.ExampleFirstLine)} to {N(selector.ExampleLastLine)} in the example";
            var word = CsvPartLocator.DelimiterWord(expected.Delimiter);
            Delimiter = char.ToUpperInvariant(word[0]) + word[1..];
            Header = expected.HasHeader ? "Yes" : "No";
            SkipLines = N(expected.SkipLines);
            Columns = N(expected.ColumnCount);
            ColumnNames = string.Join(", ", expected.ColumnNames);
            ExampleSha256 = selector.ExampleSha256.Length > 12 ? selector.ExampleSha256[..12] : selector.ExampleSha256;
            var kept = node.ColumnSelection.KeptColumns(expected.ColumnNames).Count;
            KeptText = kept == expected.ColumnCount ? "All columns" : $"{N(kept)} of {N(expected.ColumnCount)} columns";
        }

        OpenIntakeStudioToolTip = hasFileIntake ? "Change the part in Intake Studio, then press Use in pipeline." : NoFileIntakeText;
        var partReady = location is { Ok: true, Part: not null } && node.KnownColumns is not null;
        ChooseColumnsToolTip = partReady ? "Choose which columns of the part go on to ParseValue." : PartNotReadText;

        OpenIntakeStudioCommand = new RelayCommand(_ => Finish(CsvPartSettingsNext.OpenIntakeStudio), _ => hasFileIntake);
        ChooseColumnsCommand = new RelayCommand(_ => Finish(CsvPartSettingsNext.ChooseColumns), _ => partReady);
        CloseCommand = new RelayCommand(_ => Finish(CsvPartSettingsNext.None));
    }

    public string Title { get; }

    public string ExampleFile { get; }

    public string TemplateName { get; }

    public string PartText { get; }

    public string Delimiter { get; }

    public string Header { get; }

    public string SkipLines { get; }

    public string Columns { get; }

    public string ColumnNames { get; }

    /// <summary>The first 12 characters of the example file's SHA-256.</summary>
    public string ExampleSha256 { get; }

    /// <summary>"All columns" or "3 of 5 columns" (the Data Studio choice).</summary>
    public string KeptText { get; }

    public string Status { get; }

    public string OpenIntakeStudioToolTip { get; }

    public string ChooseColumnsToolTip { get; }

    public RelayCommand OpenIntakeStudioCommand { get; }

    public RelayCommand ChooseColumnsCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>What the host does after the dialog closed.</summary>
    public CsvPartSettingsNext Next { get; private set; }

    public event EventHandler? CloseRequested;

    private void Finish(CsvPartSettingsNext next)
    {
        Next = next;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
