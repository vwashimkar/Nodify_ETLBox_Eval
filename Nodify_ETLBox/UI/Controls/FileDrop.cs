using System.Windows;
using System.Windows.Input;

namespace EtlDesigner.UI.Controls;

/// <summary>
/// Lets one file be dropped on an element: the path goes to <see cref="CommandProperty"/> (task G7a). Only a
/// single file is accepted; folders and several files show the "not allowed" cursor.
/// </summary>
public static class FileDrop
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(FileDrop), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;
        if (e.NewValue is not null)
        {
            element.AllowDrop = true;
            element.DragOver += OnDragOver;
            element.Drop += OnDrop;
        }
    }

    /// <summary>The one file being dragged, or null.</summary>
    internal static string? OneFile(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && System.IO.File.Exists(paths[0])
            ? paths[0]
            : null;

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var command = GetCommand((DependencyObject)sender);
        e.Effects = OneFile(e.Data) is { } path && command?.CanExecute(path) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        var command = GetCommand((DependencyObject)sender);
        if (OneFile(e.Data) is { } path && command?.CanExecute(path) == true)
        {
            command.Execute(path);
        }

        e.Handled = true;
    }
}
