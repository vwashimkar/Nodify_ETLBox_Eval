using System.Windows;
using System.Windows.Input;

namespace EtlDesigner.UI.Controls;

/// <summary>
/// Runs a command on a double-click with the left mouse button on a canvas node.
/// Nodify's item container takes the mouse for selection and dragging, so a MouseBinding on the
/// node may never see the second click [U]. This listens to the tunnelling PreviewMouseLeftButtonDown
/// (also when it is already handled), which reaches the node before Nodify's container acts on it.
/// No command (null) means no double-click action.
/// </summary>
public static class NodeDoubleClick
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command",
        typeof(ICommand),
        typeof(NodeDoubleClick),
        new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static readonly MouseButtonEventHandler Handler = OnPreviewMouseLeftButtonDown;

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, Handler);
        if (e.NewValue is not null)
        {
            element.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, Handler, handledEventsToo: true);
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not DependencyObject element || GetCommand(element) is not { } command)
        {
            return;
        }

        if (command.CanExecute(null))
        {
            // Run after the mouse event, so a modal dialog does not open in the middle of Nodify's mouse handling.
            e.Handled = true;
            element.Dispatcher.BeginInvoke(() => command.Execute(null));
        }
    }
}
