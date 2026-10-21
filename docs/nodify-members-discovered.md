# Nodify 7.3.0 Members Discovered & Verification Notes

## Overview
This document lists Nodify types and members discovered from Nodify.xml documentation during the graph editor implementation. These members represent the programmatic API surface available in Nodify 7.3.0 for net10.0-windows7.0.

## Core Types

### NodifyEditor (Main Container Control)
- **Item Bindings**: `ItemsSource` (nodes), `Connections`, `PendingConnection`
- **Commands**:
  - `ConnectionStartedCommand`: Fired when pending connection starts
  - `ConnectionCompletedCommand`: Fired when connection completes; parameter is `Tuple<Source, Target>`
  - `DisconnectConnectorCommand`: Fired on connector disconnect

### Connector
- **Events**:
  - `PendingConnectionStarted`: Connection drawing initiated
  - `PendingConnectionDrag`: Connection being dragged
  - `PendingConnectionCompleted`: Connection completed
- **Methods**:
  - `UpdatePendingConnection(Point)`: Update with point coordinate
  - `UpdatePendingConnection(Vector)`: Update with vector offset
- **Properties**:
  - `AllowPendingConnectionCancellation`: bool
  - `IsPendingConnection`: bool

### PendingConnection (In-Progress Connection Visual)
- **Properties**:
  - `Source`: object (the source connector)
  - `Target`: object (target connector, set at completion)
  - `PreviewTarget`: object (potential target being hovered; Connector or ItemContainer)
  - `EnableHitTesting`: bool (allows preview target detection)
  - `EnablePreview`: bool (updates PreviewTarget during drag)
  - `EnableSnapping`: bool (snaps TargetAnchor to Target connector)
  - `AllowOnlyConnectors`: bool (restricts PreviewTarget to Connector type; allows ItemContainer if false)
  - `SourceAnchor`: object (source position)
  - `TargetAnchor`: object (target position, follows mouse)
  - `Stroke`: Brush (line color)
  - `StrokeThickness`: double
  - `StrokeDashArray`: DoubleCollection (dash pattern for line)
  - `IsVisible`: bool
  - `Direction`: object (presumably flow direction)
  - `Editor`: NodifyEditor (parent editor reference)
  - `HotKeysDisplayMode`: object (display mode for keyboard hints)
  - `MaxHotKeys`: int (limit on displayed hotkeys)
- **Commands**:
  - `CompletedCommand`: ICommand fired on connection completion
  - `StartedCommand`: ICommand fired on connection start
- **Methods**:
  - `FindConnectionTarget(Point)`: Locate connector at given point
  - `GetPotentialConnector(NodifyEditor, Point)`: Identify potential target connector
  - `GetPotentialConnector(NodifyEditor, Point, bool)`: Identify with optional filter
  - `SetConnectionTarget(FrameworkElement)`: Explicitly set target element
  - `OnApplyTemplate()`: Template application hook

### Connection (Completed Connection Visual)
- **Properties** (from Command parameters):
  - Source (from Tuple<Item1, Item2>)
  - Target (from Tuple<Item1, Item2>)
- **Visual**:
  - `Stroke`: Brush
  - `StrokeThickness`: double
  - `ArrowEnds`: enum (End, Start, Both, None) for arrowhead placement

### Node & ItemContainer
- **Referenced in**: PendingConnection.PreviewTarget can be either Connector or ItemContainer (depending on AllowOnlyConnectors)
- **Implication**: Graph nodes should have an ItemContainer concept or explicit DataContext for targeting

### StateNode
- **Property**:
  - `HighlightBrush`: Brush applied when `PendingConnection.IsOverElementProperty` is true
  - `Content`: object (node content)

## Events Container

### PendingConnectionEventArgs
- **Properties**:
  - `SourceConnector`: Connector (the initiating connector)
  - `TargetConnector`: Connector (destination, if hit)
  - `Anchor`: Point (position reference)
  - `OffsetX`, `OffsetY`: double (position offset from anchor)
  - `Canceled`: bool (connection was canceled)

### PendingConnectionEventHandler
- Type alias for event handler used by PendingConnection events

## Attached Properties & Fields

### IsOverElementProperty (PendingConnection Attached)
- **Type**: AttachedProperty<bool>
- **Usage**: Applied to elements that are valid connection targets; used by StateNode.HighlightBrush

## Implementation Notes

### Not Used in This Step
- ETLBox types (ETLBox, ETLBox.Csv, ETLBox.Json integration)
- Direct Nodify XAML control rendering (NodifyEditor, Node, NodeInput, NodeOutput, etc.)
- Connection visual rendering with actual Nodify connection lines
- Drag-and-drop integration with Nodify gestures
- Keyboard hotkeys and modifier key handling
- Template selectors for connection/node rendering

### Workaround Strategy Applied
The implementation uses WPF ItemsControl + Canvas for node layout and Border element for individual node rendering, bypassing Nodify's XAML controls. All Nodify programmatic types (Connector, PendingConnection, NodifyEditor commands) can be accessed in code-behind if the XAML namespace issue is resolved. The ViewModels remain decoupled from Nodify rendering, enabling future integration.

### Build Status
✅ Warning-free build achieved with net10.0-windows target, nullable enabled, file-scoped namespaces.
✅ Complete graph model (nodes, connectors, connections, validation rules) functional.
❌ Nodify XAML controls (NodifyEditor, Node, Connection visuals) not integrated due to namespace resolution failure.
⚠️ Pending real Nodify control API verification or alternative rendering documentation.
