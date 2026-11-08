using System.Windows;
using EtlDesigner.UI.Services;
using EtlDesigner.UI.ViewModels;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>The file types the open-file dialogs offer (G4c step 4).</summary>
public sealed class FileFiltersTests
{
    /// <summary>The (name, patterns) pairs of a filter string, in order.</summary>
    private static List<(string Name, string Patterns)> Pairs(string filter)
    {
        var parts = filter.Split('|');
        Assert.True(parts.Length % 2 == 0, "A filter string has a name and a pattern for every entry.");
        return Enumerable.Range(0, parts.Length / 2).Select(i => (parts[2 * i], parts[2 * i + 1])).ToList();
    }

    [Fact]
    public void Intake_OffersAllFilesFirst_ThenTextAndData_ThenCsv()
    {
        var pairs = Pairs(FileFilters.Intake);

        Assert.Equal(3, pairs.Count);
        Assert.Equal(("All files (*.*)", "*.*"), pairs[0]);
        Assert.StartsWith("Text and data files", pairs[1].Name, StringComparison.Ordinal);
        Assert.Equal("*.txt;*.csv;*.tsv;*.json;*.jsonl;*.xml;*.yaml;*.yml;*.log;*.dat", pairs[1].Patterns);
        Assert.Equal(("CSV files (*.csv)", "*.csv"), pairs[2]);
    }

    [Fact]
    public void Csv_IsUnchanged_CsvFirst()
        => Assert.Equal([("CSV files (*.csv)", "*.csv"), ("All files (*.*)", "*.*")], Pairs(FileFilters.Csv));

    [Fact]
    public void FileIntakeNode_UsesTheIntakeList_CsvFileNode_StillStartsWithCsv()
    {
        var intake = NodeFactory.Create(NodeType.FileIntake, new Point());
        var csv = NodeFactory.Create(NodeType.CsvFile, new Point());

        Assert.Equal(FileFilters.Intake, intake.Settings.Single(s => s.Kind == SettingKind.FilePath).FileFilter);
        var csvFilter = csv.Settings.Single(s => s.Kind == SettingKind.FilePath).FileFilter;
        Assert.Equal(FileFilters.Csv, csvFilter);
        Assert.StartsWith("CSV files (*.csv)|", csvFilter, StringComparison.Ordinal);
    }
}
