using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EtlDesigner.UI.ViewModels;
using System.IO;
using System.Threading.Tasks;

namespace Nodify_ETLBox;

public partial class MainWindow : Window
{
    private readonly MainViewModel _mainViewModel;

    private Point _dragStart;

    public MainWindow()
    {
        InitializeComponent();

        // Set the field BEFORE anything can use it. The previous OnInitialized override
        // ran inside InitializeComponent(), when DataContext was still null, so the
        // field stayed null and every click handler returned silently.
        _mainViewModel = new MainViewModel();
        DataContext = _mainViewModel;
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Swap Nodify theme dictionaries
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var currentTheme = ThemeSelector.SelectedIndex == 1 ? "Dark" : "Light";

        // Remove old Nodify theme
        var oldTheme = dictionaries.FirstOrDefault(d => d.Source?.OriginalString?.Contains("Themes/") == true && d.Source.OriginalString.Contains(".xaml"));
        if (oldTheme != null)
        {
            dictionaries.Remove(oldTheme);
        }

        // Add new Nodify theme
        var themeUri = new Uri($"Themes/{currentTheme}.xaml", UriKind.Relative);
        dictionaries.Add(new ResourceDictionary { Source = themeUri });

        // Also switch window chrome theme via RequestedTheme or window background
        if (currentTheme == "Dark")
        {
            Background = System.Windows.Media.Brushes.Gainsboro;
            Foreground = System.Windows.Media.Brushes.Black;
        }
        else
        {
            Background = System.Windows.Media.Brushes.White;
            Foreground = System.Windows.Media.Brushes.Black;
        }
    }

    // Converts a point inside the editor control (screen units) to graph coordinates.
    private Point ToGraphLocation(Point positionInEditor) => new(
        EditorControl.ViewportLocation.X + positionInEditor.X / EditorControl.ViewportZoom,
        EditorControl.ViewportLocation.Y + positionInEditor.Y / EditorControl.ViewportZoom);

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse<NodeType>(tag, out var nodeType))
            return;

        // Add at the centre of the visible editor area.
        var center = new Point(EditorControl.ActualWidth / 2, EditorControl.ActualHeight / 2);
        _mainViewModel.Editor.AddNode(nodeType, ToGraphLocation(center));
    }

    private void PaletteButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(null);

    private void PaletteButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Button { Tag: string tag } button) return;

        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop(button, tag, DragDropEffects.Copy);
    }

    private void EditorControl_Drop(object sender, DragEventArgs e)
    {
        // The drag payload is the node type name as a plain string (see PreviewMouseMove).
        if (e.Data.GetData(typeof(string)) is not string tag ||
            !Enum.TryParse<NodeType>(tag, out var nodeType))
            return;

        _mainViewModel.Editor.AddNode(nodeType, ToGraphLocation(e.GetPosition(EditorControl)));
        e.Handled = true;
    }

    private async void Spike_Click(object sender, RoutedEventArgs e)
    {
        // Temporary spike: run a hardcoded ETLBox pipeline.
        _mainViewModel.FilteredJsonOutput = "Spike: running...";
        _mainViewModel.ParsedJsonOutput = "";

        try
        {
            var csvPath = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "SampleData",
                "employee_list.csv");

            if (!System.IO.File.Exists(csvPath))
            {
                _mainViewModel.FilteredJsonOutput = "Error: CSV file not found at " + csvPath;
                return;
            }

            // Run off the UI thread so the window stays responsive.
            var result = await Task.Run(() => EtlDesigner.Spike.ETLBoxSpike.RunPipeline(csvPath, "India", ","));

            _mainViewModel.FilteredJsonOutput = result.FilteredJson;
            _mainViewModel.ParsedJsonOutput = result.ParsedJson;
        }
        catch (Exception ex)
        {
            _mainViewModel.FilteredJsonOutput = $"Error: {ex.Message}\n\n{ex.StackTrace}";
            _mainViewModel.ParsedJsonOutput = "";
        }
    }

    private async void RunAllTests_Click(object sender, RoutedEventArgs e)
    {
        // Run all tests from the test harness.
        _mainViewModel.FilteredJsonOutput = "Running all tests...";
        _mainViewModel.ParsedJsonOutput = "";
        _mainViewModel.Editor.Issues.Clear();

        try
        {
            var sampleDataDir = Path.Combine(AppContext.BaseDirectory, "SampleData");
            var runner = new EtlDesigner.Spike.TestRunner(sampleDataDir);
            var results = await runner.RunAllTests();

            // Write results to CSV
            var resultsPath = Path.Combine(AppContext.BaseDirectory, "SpikeResults.csv");
            WriteSpikeResultsCsv(resultsPath, results);

            // Display results in Issues pane
            int passCount = 0, failCount = 0, observedCount = 0;
            var failDetails = new List<string>();

            foreach (var result in results)
            {
                if (result.Result == "PASS") passCount++;
                else if (result.Result == "FAIL")
                {
                    failCount++;
                    failDetails.Add($"{result.TestId}: {result.Detail}");
                }
                else if (result.Result == "OBSERVED") observedCount++;
            }

            // Add summary to issues
            var summary = $"{passCount} pass / {failCount} fail / {observedCount} observed";
            _mainViewModel.Editor.Issues.Add(summary);

            // Add fail details
            foreach (var detail in failDetails)
            {
                _mainViewModel.Editor.Issues.Add($"  {detail}");
            }

            _mainViewModel.FilteredJsonOutput = $"Test results written to {resultsPath}";
            _mainViewModel.ParsedJsonOutput = summary;
        }
        catch (Exception ex)
        {
            _mainViewModel.FilteredJsonOutput = $"Error running tests: {ex.Message}\n\n{ex.StackTrace}";
            _mainViewModel.Editor.Issues.Add($"Test error: {ex.Message}");
        }
    }

    private void WriteSpikeResultsCsv(string path, List<EtlDesigner.Spike.TestResult> results)
    {
        using (var writer = new StreamWriter(path))
        {
            // Write header
            writer.WriteLine("test_id,file,country,expectation,actual_filtered,actual_parsed,actual_skipped,result,detail,elapsed_ms,license_file_present");

            // Write results
            foreach (var result in results)
            {
                var licenseStr = result.LicenseFilePresent ? "true" : "false";
                var fields = new[]
                {
                    CsvEscape(result.TestId),
                    CsvEscape(result.File),
                    CsvEscape(result.Country),
                    CsvEscape(result.Expectation),
                    result.ActualFiltered?.ToString() ?? "",
                    result.ActualParsed?.ToString() ?? "",
                    result.ActualSkipped?.ToString() ?? "",
                    CsvEscape(result.Result),
                    CsvEscape(result.Detail),
                    result.ElapsedMs.ToString(),
                    licenseStr
                };
                writer.WriteLine(string.Join(",", fields));
            }
        }
    }

    private string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.Contains("\"") || value.Contains(",") || value.Contains("\n"))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
