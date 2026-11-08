namespace EtlDesigner.UI.Services;

/// <summary>The file-type lists of the open-file dialogs, in one place. The first entry is selected when a dialog opens.</summary>
public static class FileFilters
{
    /// <summary>CsvFile and the other CSV settings: CSV first.</summary>
    public const string Csv = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";

    /// <summary>File intake (the node's "..." button and Browse in Intake Studio): any file first, an instrument export can have any extension.</summary>
    public const string Intake =
        "All files (*.*)|*.*"
        + "|Text and data files (*.txt;*.csv;*.tsv;*.json;*.jsonl;*.xml;*.yaml;*.yml;*.log;*.dat)|*.txt;*.csv;*.tsv;*.json;*.jsonl;*.xml;*.yaml;*.yml;*.log;*.dat"
        + "|CSV files (*.csv)|*.csv";
}
