using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EtlDesigner.UI.ViewModels;

namespace Nodify_ETLBox;

public partial class MainWindow : Window
{
    private MainViewModel? _mainViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        if (DataContext is not MainViewModel mainVm)
            return;

        _mainViewModel = mainVm;
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

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || _mainViewModel?.Editor is not EditorViewModel editor)
            return;

        var nodeTypeStr = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(nodeTypeStr) || !Enum.TryParse<NodeType>(nodeTypeStr, out var nodeType))
            return;

        // Position new node at center of editor
        editor.AddNode(nodeType, new System.Windows.Point(400, 300));
    }

    private void PaletteButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is Button btn)
        {
            var nodeTypeStr = btn.Tag?.ToString();
            if (!string.IsNullOrEmpty(nodeTypeStr) && Enum.TryParse<NodeType>(nodeTypeStr, out var nodeType))
            {
                var data = new DataObject();
                data.SetData("NodeType", nodeType);
                DragDrop.DoDragDrop(btn, data, DragDropEffects.Copy);
            }
        }
    }

    private void EditorControl_Drop(object sender, DragEventArgs e)
    {
        if (_mainViewModel?.Editor is not EditorViewModel editor)
            return;

        if (e.Data.GetData("NodeType") is NodeType nodeType)
        {
            // Get position relative to NodifyEditor
            var point = e.GetPosition(EditorControl);
            editor.AddNode(nodeType, point);
            e.Handled = true;
        }
    }
}
