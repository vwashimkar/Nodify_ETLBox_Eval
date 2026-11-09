using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace EtlDesigner.UI.Controls;

/// <summary>
/// Ctrl+Z and Ctrl+Y for a window's layout undo and redo, but only when the key does not come from
/// a text box: there Ctrl+Z belongs to the box's own text. The key is handled after the focused
/// control had its turn (bubbling KeyDown), so a text box that undoes its text marks it handled first.
/// </summary>
public static class LayoutUndoKeys
{
    public static readonly DependencyProperty UndoProperty = DependencyProperty.RegisterAttached(
        "Undo", typeof(ICommand), typeof(LayoutUndoKeys), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty RedoProperty = DependencyProperty.RegisterAttached(
        "Redo", typeof(ICommand), typeof(LayoutUndoKeys), new PropertyMetadata(null, OnChanged));

    public static ICommand? GetUndo(DependencyObject element) => (ICommand?)element.GetValue(UndoProperty);

    public static void SetUndo(DependencyObject element, ICommand? value) => element.SetValue(UndoProperty, value);

    public static ICommand? GetRedo(DependencyObject element) => (ICommand?)element.GetValue(RedoProperty);

    public static void SetRedo(DependencyObject element, ICommand? value) => element.SetValue(RedoProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.KeyDown -= OnKeyDown;
            element.KeyDown += OnKeyDown;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled && sender is DependencyObject target && e.OriginalSource is DependencyObject source)
        {
            e.Handled = Handle(target, source, e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        }
    }

    /// <summary>
    /// Runs the layout undo (Ctrl+Z) or redo (Ctrl+Y, Ctrl+Shift+Z) of <paramref name="target"/> for a
    /// key from <paramref name="source"/>. False, and nothing done, when the source is in a text box.
    /// </summary>
    public static bool Handle(DependencyObject target, DependencyObject source, Key key, ModifierKeys modifiers)
    {
        if (InTextBox(source))
        {
            return false;
        }

        var command = (key, modifiers) switch
        {
            (Key.Z, ModifierKeys.Control) => GetUndo(target),
            (Key.Y, ModifierKeys.Control) or (Key.Z, ModifierKeys.Control | ModifierKeys.Shift) => GetRedo(target),
            _ => null
        };

        if (command is null || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private static bool InTextBox(DependencyObject? element)
    {
        for (var current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is TextBoxBase)
            {
                return true;
            }
        }

        return false;
    }
}
