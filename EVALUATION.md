# Evaluation Report

## Nodify

### What worked well

- **Package availability and targeting**: Nodify 7.3.0 is available for net10.0-windows7.0 and installs cleanly without assembly conflicts or transitive dependency issues.
- **XML documentation**: Nodify ships with comprehensive IntelliSense XML (Nodify.xml) that documents all types, properties, methods, and events.
- **API design consistency**: The PendingConnection, Connector, NodifyEditor, StateNode, ItemContainer, LineConnection types follow consistent property naming and event patterns.
- **Real example projects available**: Nodify.StateMachine example in the reference/ folder provided exact working XAML markup for NodifyEditor, templates, and item container styling.
- **XAML integration verified**: Once correct XAML namespace and property names were confirmed, NodifyEditor, PendingConnection, LineConnection, StateNode, and ItemContainer all resolve and compile without error.
- **Nodes drag-enabled out-of-box**: ItemContainer handles node positioning via Location binding; Nodify manages drag gesture and updates automatically.
- **Connection visual rendering**: LineConnection control renders connection lines with configurable stroke, thickness, and anchors.
- **Pending connection preview**: PendingConnection with EnablePreview="True" updates TargetAnchor in real-time during drag, enabling visual preview of in-progress connections.
- **Build success & runtime verification**: Application compiles warning-free and launches successfully; node dragging via ItemContainer.Location binding works as expected without custom drag code.

### Friction and workarounds

1. **Initial namespace confusion & property guessing**: The XAML namespace http://miroiu.github.io/nodify is correct, but without a working example, properties like Editor, ShowGrid, SnapToGrid (non-existent) were guessed.
   - **What I tried**: Used training knowledge to invent plausible Nodify properties.
   - **What went wrong**: MC3074 errors when no NodifyEditor tag resolved at all; guessed properties failed at compile.
   - **Workaround**: Located reference/Nodify.StateMachine/MainWindow.xaml example and copied exact working XAML structure: ItemsSource, SelectedItem(s), Connections, PendingConnection binding.
   - **Severity**: Medium—blocked until reference example was found, but once located, all properties verified immediately.

2. **NodeInput / NodeOutput controls with Anchor binding**: Nodify Node type used for complex node rendering with input/output connector templates.
   - **What I tried**: Bound NodeInput/NodeOutput Header, IsConnected, Anchor properties per reference example.
   - **What went wrong**: Initially thought Connector property existed; it does not. Instead, Anchor is bound directly.
   - **Workaround**: Used Header={Binding Name}, IsConnected={Binding IsConnected}, Anchor={Binding Anchor, Mode=OneWayToSource} on NodeInput/NodeOutput.
   - **Severity**: Low—resolved by reference inspection; now working correctly.

3. **PendingConnectionViewModel requires Source and Target properties**: Nodify PendingConnectionTemplate binds Source/Target with OneWayToSource mode.
   - **What I tried**: PendingConnectionViewModel initially only had PreviewTarget.
   - **What went wrong**: XAML binding failed because Source and Target were missing.
   - **Workaround**: Added Source and Target properties to PendingConnectionViewModel, along with UpdateIsValid() logic.
   - **Severity**: Low—added after XAML inspection revealed the binding requirement.

4. **EditorViewModel.DisconnectConnectorCommand was missing**: XAML bound DisconnectConnectorCommand to Editor.DisconnectConnectorCommand, but it didn't exist.
   - **What I tried**: Added DisconnectConnectorCommand property to EditorViewModel.
   - **What went wrong**: Not initially present; required to handle connection deletion.
   - **Workaround**: Implemented DisconnectConnectorCommand as a RelayCommand that accepts ConnectionViewModel and calls Disconnect().
   - **Severity**: Low—discovered during XAML binding verification.

5. **SettingViewModel.IsMissing property needed for red asterisk display**: XAML bound Visibility={Binding IsMissing}.
   - **What I tried**: Added IsMissing as a computed property: Required && string.IsNullOrEmpty(Value).
   - **What went wrong**: When Value changed, IsMissing would not update in XAML because it's not a property with change notification.
   - **Workaround**: Ensured Value uses SetProperty(), which triggers PropertyChanged. IsMissing computed property is re-evaluated on every Value change.
   - **Severity**: Low—binding works correctly with this pattern; XAML will refresh when Value changes.

6. **SettingViewModel.BrowseCommand for file selection**: XAML bound BrowseCommand to FilePath setting template button.
   - **What I tried**: Added BrowseCommand as RelayCommand that opens OpenFileDialog (WPF, not WinForms).
   - **What went wrong**: Initially used System.Windows.Forms.OpenFileDialog, which requires WinForms assembly.
   - **Workaround**: Switched to Microsoft.Win32.OpenFileDialog (WPF native, no extra assembly).
   - **Severity**: Low—resolved by using correct WPF OpenFileDialog.

7. **Nodify Node type with Footer binding**: XAML Node Footer displays State and RowCount.
   - **What I tried**: Bound Node.FooterBrush to State (NodeState enum) via converter.
   - **What went wrong**: Converter expects NodeState and returns Brush; resolver automatically applied due to type mismatch.
   - **Workaround**: NodeStateToColorConverter already existed; used as-is. Works correctly.
   - **Severity**: Low—converter was already implemented.

8. **Connection.Source/Target binding to Anchor points**: Reference XAML showed Source={Binding Source.Anchor}, Target={Binding Target.Anchor}.
   - **What I tried**: Bound Connection control to source and target Anchors (Points).
   - **What went wrong**: Initially thought Source/Target were Connectors; they are, but Anchor is the point used for rendering.
   - **Workaround**: Used Source={Binding Source.Anchor} and Target={Binding Target.Anchor} in ConnectionTemplate.
   - **Severity**: Low—reference example clarified the binding immediately.

9. **NodifyEditor SelectedItem/SelectedItems binding re-entrancy**: When both SelectedItem and SelectedItems are bound to the ViewModel, Nodify's internal state mutation of SelectedItems during a user selection triggers PropertyChanged on SelectedItem, which pushes back to Nodify.SelectedItems, causing InvalidOperationException "Cannot change ObservableCollection during a CollectionChanged event."
   - **What I tried**: Bound NodifyEditor.SelectedItem to Editor.SelectedNode and NodifyEditor.SelectedItems to Editor.SelectedNodes, with a CollectionChanged handler on SelectedNodes that set SelectedNode.
   - **What went wrong**: Nodify mutates SelectedItems collection during user click/drag. The SelectedNodes.CollectionChanged handler fired, which set SelectedNode property, which bound back to SelectedItem, which tried to modify SelectedItems re-entrantly.
   - **Workaround**: Remove the SelectedItem binding entirely. Make SelectedNode a read-only property computed from SelectedNodes (SelectedNodes.Count == 1 ? SelectedNodes[0] : null). Do not set SelectedNode from handlers. Iterate over a snapshot of SelectedNodes in DeleteSelected to avoid Collection mutating during iteration.
   - **Severity**: High—blocks UI interaction on node selection. Fix: only bind SelectedItems, remove SelectedItem binding, use computed SelectedNode property.

### API gaps

- **NodeInput / NodeOutput connector resolution in commands**: When ConnectionCompletedCommand fires with Tuple<object, object>, resolving those objects to ConnectorViewModels is not straightforward. Current workaround uses DataContext introspection.
- **PendingConnection source/target mode**: Source and Target are bound with Mode=OneWayToSource, updating the ViewModel from control. This is counterintuitive for a pending connection that already knows its source.
- **Nodify Connection parameter shape in commands**: ConnectionCompletedCommand passes a tuple, not a ConnectorPair or structured object. Requires manual unpacking.
- **No direct Nodify→ViewModel state sync**: Creating a ConnectionViewModel and adding it to Connections doesn't automatically update Nodify's internal state. Must manually update IsConnected flags.
- **SettingViewModel.IsMissing must be computed**: No automatic change notification when a property is declared computed (Required && !Value.IsEmpty). Relies on Value using SetProperty to invalidate dependents.

### Documentation quality

- **XML IntelliSense**: Excellent for types and properties (e.g., PendingConnection.EnablePreview, LineConnection properties, Node template structure).
- **Reference examples lacking**: Nodify.xml has no inline XAML examples. Knowledge of Node template structure, connector templates, and PendingConnectionTemplate must come from reference projects.
- **Node template structure not documented**: Reference XAML showed InputConnectorTemplate/OutputConnectorTemplate/Footer structure; XML docs don't explain this hierarchy.
- **XAML property names**: Once located in reference projects, property names and bindings follow WPF conventions exactly (ItemsSource, SelectedItem, etc.).

### Open questions

1. **Node type variants**: Is StateNode the only node container type, or are there alternatives for custom layouts?
2. **Connector visual ports**: Are NodeInput/NodeOutput port visuals required for user interaction, or are they optional decorations?
3. **Connection lifecycle**: Should IsConnected be set automatically by Nodify when a connection is rendered, or must the ViewModel manage it?
4. **Pending connection target resolution**: Is there an official API to resolve Nodify Connector objects back to ConnectorViewModels, or is DataContext introspection the recommended pattern?
5. **Nodify property binding modes**: Why does PendingConnection use Source={Binding Source, Mode=OneWayToSource}? Should it be TwoWay to reflect Nodify's internal state changes?

## ETLBox

### What worked well

(Not used in this step per user requirement.)

### Friction and workarounds

(Not used in this step per user requirement.)

### API gaps

(Not used in this step per user requirement.)

### Documentation quality

(Not used in this step per user requirement.)

### Open questions

(Not used in this step per user requirement.)



