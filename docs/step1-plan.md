# Step 1: Nodify Graph Editor Implementation Plan

## Objectives
1. Build a complete graph editor using Nodify 7.3.0 (net10.0-windows)
2. Implement five hardcoded node types with full MVVM ViewModels
3. Full connection model with validation rules
4. Use real Nodify controls (NodifyEditor, Node, NodeInput, NodeOutput)
5. Theme switching for both Nodify theme AND window chrome
6. Drag-and-drop palette support
7. State-based node coloring (Idle grey, Running blue, Completed green, Failed red)
8. Visual connections with arrowheads; pending connection green/red for valid/invalid
9. Delete key binding (XAML-based, not code-behind logic)
10. Required field red markers in settings panel

## Key Findings from Nodify 7.3.0 API (XML Documentation)

### PendingConnection Type
- **PreviewTarget**: `object` (can be `Connector` or `ItemContainer` depending on `AllowOnlyConnectors` property)
- **Source**: `object` (the source connector)
- **Target**: `object` (the target connector, set at completion)
- **CompletedCommand**: ICommand (fired when connection completed with Tuple<Source, Target>)
- **StartedCommand**: ICommand (fired on connection start)
- **EnablePreview**, **EnableHitTesting**, **EnableSnapping**: bool properties
- **Stroke**, **StrokeThickness**, **StrokeDashArray**: Visual connction properties
- **IsVisible**: Controls visibility of pending connection

### NodifyEditor Type
- **PendingConnection**: Gets/sets the PendingConnection UI control
- **PendingConnectionTemplate**: Template for pending connection rendering
- **PendingConnectionTemplateSelector**: Template selector for pending connections
- **ConnectionCompletedCommand**: ICommand fired when connection completes
- **DisconnectConnectorCommand**: ICommand fired on disconnect

### Key Events/Commands on Connector
- **PendingConnectionStarted**, **PendingConnectionDrag**, **PendingConnectionCompleted**: Events
- **AllowPendingConnectionCancellation**: bool property

## Implementation Strategy

1. Keep all ViewModels as planned (PortKind, ConnectorViewModel, NodeViewModel, etc.)
2. Update PendingConnectionViewModel.PreviewTarget type to `object` (matching Nodify API)
3. Bind MainWindow to real Nodify NodifyEditor control
4. Create Nodify connection visual template with arrowheads
5. Implement pending connection validity styling (green/red borders or strokes)
6. Add KeyBinding in XAML for Delete key (bound to EditorViewModel.DeleteSelectedCommand)
7. Wire drag-and-drop palette buttons
8. Use DataTemplateSelector for settings with red asterisk helper
9. Theme switching via Nodify theme resource + window chrome styles
10. Add Run button to call Validate command

## Current State
- XAML/code files exist but use generic WPF components (Canvas + Border + ItemsControl)
- No real Nodify controls yet
- Need to replace with actual Nodify NodifyEditor, Node, NodeInput, NodeOutput
- Need to wire all Nodify commands/events
- Need to create proper templates for Nodify integration

## Next Steps
1. Verify Nodify XML namespace URL (http://miroiu.github.io/nodify expected)
2. Update MainWindow.xaml to use NodifyEditor with correct bindings
3. Create Nodify node template with Node, NodeInput, NodeOutput controls
4. Create connection template with arrowheads using Path/Polygon
5. Update PendingConnectionViewModel to handle real Nodify PreviewTarget (object type)
6. Add Delete key KeyBinding in XAML
7. Implement connection validation visual feedback
8. Test build and validate all components
9. Update EVALUATION.md with findings and any friction
