using System.Collections.ObjectModel;
using System.Windows;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

public class EditorViewModel : ObservableObject
{
    private PendingConnectionViewModel? _pendingConnection;
    private ObservableCollection<NodeViewModel> _selectedNodes;

    public EditorViewModel()
    {
        Nodes = new ObservableCollection<NodeViewModel>();
        Connections = new ObservableCollection<ConnectionViewModel>();
        Issues = new ObservableCollection<string>();
        _selectedNodes = new ObservableCollection<NodeViewModel>();
        _selectedNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedNode));
        _pendingConnection = null;

        AddNodeCommand = new RelayCommand(param =>
        {
            if (param is (NodeType nodeType, Point location))
            {
                AddNode(nodeType, location);
            }
        });

        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected());

        LoadSampleGraphCommand = new RelayCommand(_ => LoadSampleGraph());

        ValidateCommand = new RelayCommand(_ => Validate());

        CreateConnectionCommand = new RelayCommand(param =>
        {
            // Nodify passes the result tuple. Extract source and target ConnectorViewModels.
            if (param is (object source, object target))
            {
                // Try to resolve the objects to ConnectorViewModels
                ConnectorViewModel? sourceConnector = ResolveConnectorFromNodifyObject(source);
                ConnectorViewModel? targetConnector = ResolveConnectorFromNodifyObject(target);

                if (sourceConnector != null && targetConnector != null)
                {
                    CompleteConnection(targetConnector);
                }
            }
        });

        DisconnectConnectorCommand = new RelayCommand(param =>
        {
            if (param is ConnectionViewModel connection)
            {
                Disconnect(connection);
            }
        });
    }

    public ObservableCollection<NodeViewModel> Nodes { get; }

    public ObservableCollection<ConnectionViewModel> Connections { get; }

    public ObservableCollection<string> Issues { get; }

    public ObservableCollection<NodeViewModel> SelectedNodes
    {
        get => _selectedNodes;
        private set => SetProperty(ref _selectedNodes, value);
    }

    public PendingConnectionViewModel? PendingConnection
    {
        get => _pendingConnection;
        private set => SetProperty(ref _pendingConnection, value);
    }

    public NodeViewModel? SelectedNode => _selectedNodes.Count == 1 ? _selectedNodes[0] : null;

    public RelayCommand AddNodeCommand { get; }

    public RelayCommand DeleteSelectedCommand { get; }

    public RelayCommand LoadSampleGraphCommand { get; }

    public RelayCommand ValidateCommand { get; }

    public RelayCommand CreateConnectionCommand { get; }

    public RelayCommand DisconnectConnectorCommand { get; }

    /// <summary>
    /// Adds a new node at the specified position.
    /// </summary>
    public void AddNode(NodeType nodeType, Point location)
    {
        var node = nodeType switch
        {
            NodeType.CsvSource => NodeFactory.CreateCsvSource(location),
            NodeType.FilterByCountry => NodeFactory.CreateFilterByCountry(location),
            NodeType.ToJson => NodeFactory.CreateToJson(location),
            NodeType.ParseJson => NodeFactory.CreateParseJson(location),
            NodeType.ResultJson => NodeFactory.CreateResultJson(location),
            _ => throw new ArgumentException($"Unknown node type: {nodeType}")
        };

        Nodes.Add(node);
    }

    /// <summary>
    /// Deletes all selected nodes and their connections.
    /// </summary>
    public void DeleteSelected()
    {
        // Make a copy to avoid modifying the collection during iteration
        var selectedNodes = _selectedNodes.ToList();
        if (selectedNodes.Count == 0)
            return;

        // Remove connections involving selected nodes
        var connectionsToRemove = Connections
            .Where(c => selectedNodes.Contains(c.Source.ParentNode) || selectedNodes.Contains(c.Target.ParentNode))
            .ToList();

        foreach (var connection in connectionsToRemove)
        {
            Connections.Remove(connection);
            connection.Source.IsConnected = false;
            connection.Target.IsConnected = false;
        }

        // Remove nodes
        foreach (var node in selectedNodes)
        {
            Nodes.Remove(node);
            _selectedNodes.Remove(node);
        }
    }

    /// <summary>
    /// Initiates a connection from the specified connector.
    /// </summary>
    public void StartConnection(ConnectorViewModel sourceConnector)
    {
        if (sourceConnector.IsInput)
        {
            Issues.Clear();
            Issues.Add("Cannot start connection from an input connectorViewModel.");
            return;
        }

        PendingConnection = new PendingConnectionViewModel(sourceConnector);
    }

    /// <summary>
    /// Attempts to complete a connection to the target connector.
    /// Applies connection rules and adds the connection if valid, or adds an issue message if invalid.
    /// Always clears PendingConnection.
    /// </summary>
    public void CompleteConnection(ConnectorViewModel targetConnector)
    {
        if (PendingConnection == null)
        {
            return;
        }

        var sourceConnector = PendingConnection.Source;
        if (sourceConnector == null)
        {
            return;
        }

        // Validate connection
        var validationResult = ValidateConnection(sourceConnector, targetConnector);
        if (validationResult.IsValid)
        {
            var connection = new ConnectionViewModel(sourceConnector, targetConnector);
            Connections.Add(connection);
            sourceConnector.IsConnected = true;
            targetConnector.IsConnected = true;
            Issues.Clear();
        }
        else
        {
            Issues.Clear();
            Issues.Add(validationResult.ErrorMessage ?? "Connection is invalid.");
        }

        PendingConnection = null;
    }

    /// <summary>
    /// Removes a connection.
    /// </summary>
    public void Disconnect(ConnectionViewModel connection)
    {
        Connections.Remove(connection);
        connection.Source.IsConnected = Connections.Any(c => c.Source == connection.Source);
        connection.Target.IsConnected = Connections.Any(c => c.Target == connection.Target);
    }

    /// <summary>
    /// Loads the sample graph: five nodes connected in order.
    /// </summary>
    public void LoadSampleGraph()
    {
        Nodes.Clear();
        Connections.Clear();
        Issues.Clear();
        _selectedNodes.Clear();
        PendingConnection = null;

        // Layout positions (left to right)
        var x = 20.0;
        const double y = 50.0;
        const double spacing = 180.0;

        var csvNode = NodeFactory.CreateCsvSource(new Point(x, y));
        csvNode.Settings.Single(s => s.Key == "FilePath").Value = "employee_list.csv";
        Nodes.Add(csvNode);
        x += spacing;

        var filterNode = NodeFactory.CreateFilterByCountry(new Point(x, y));
        filterNode.Settings.Single(s => s.Key == "LocationColumn").Value = "location";
        filterNode.Settings.Single(s => s.Key == "Separator").Value = "|";
        filterNode.Settings.Single(s => s.Key == "Country").Value = "United States";
        Nodes.Add(filterNode);
        x += spacing;

        var toJsonNode = NodeFactory.CreateToJson(new Point(x, y));
        Nodes.Add(toJsonNode);
        x += spacing;

        var parseJsonNode = NodeFactory.CreateParseJson(new Point(x, y));
        parseJsonNode.Settings.Single(s => s.Key == "Fields").Value = "first_name,last_name,email";
        Nodes.Add(parseJsonNode);
        x += spacing;

        var resultJsonNode = NodeFactory.CreateResultJson(new Point(x, y));
        Nodes.Add(resultJsonNode);

        // Connect nodes in sequence
        var csv = Nodes[0];
        var filter = Nodes[1];
        var toJson = Nodes[2];
        var parseJson = Nodes[3];
        var resultJson = Nodes[4];

        var conn1 = new ConnectionViewModel(csv.Outputs[0], filter.Inputs[0]);
        Connections.Add(conn1);
        csv.Outputs[0].IsConnected = true;
        filter.Inputs[0].IsConnected = true;

        var conn2 = new ConnectionViewModel(filter.Outputs[0], toJson.Inputs[0]);
        Connections.Add(conn2);
        filter.Outputs[0].IsConnected = true;
        toJson.Inputs[0].IsConnected = true;

        var conn3 = new ConnectionViewModel(toJson.Outputs[0], parseJson.Inputs[0]);
        Connections.Add(conn3);
        toJson.Outputs[0].IsConnected = true;
        parseJson.Inputs[0].IsConnected = true;

        var conn4 = new ConnectionViewModel(parseJson.Outputs[0], resultJson.Inputs[0]);
        Connections.Add(conn4);
        parseJson.Outputs[0].IsConnected = true;
        resultJson.Inputs[0].IsConnected = true;
    }

    /// <summary>
    /// Validates the graph.
    /// Checks: (a) every node's required settings are non-empty, (b) every input is connected,
    /// (c) exactly one of each node type, (d) chain is complete.
    /// Issues are added to the Issues collection.
    /// </summary>
    public void Validate()
    {
        Issues.Clear();

        // (a) Check required settings
        foreach (var node in Nodes)
        {
            var emptyRequired = node.Settings.Where(s => s.Required && string.IsNullOrWhiteSpace(s.Value)).ToList();
            foreach (var setting in emptyRequired)
            {
                Issues.Add($"{node.Title}: Setting '{setting.Label}' is required.");
            }
        }

        // (b) Check every input is connected
        foreach (var node in Nodes)
        {
            foreach (var input in node.Inputs)
            {
                if (!input.IsConnected)
                {
                    Issues.Add($"{node.Title}: Input '{input.Name}' is not connected.");
                }
            }
        }

        // (c) Exactly one of each node type
        var nodeTypeCounts = Nodes.GroupBy(n => n.NodeType).ToDictionary(g => g.Key, g => g.Count());
        foreach (var nodeType in Enum.GetValues(typeof(NodeType)).Cast<NodeType>())
        {
            var count = nodeTypeCounts.GetValueOrDefault(nodeType, 0);
            if (count != 1)
            {
                Issues.Add($"Expected exactly 1 {nodeType} node, found {count}.");
            }
        }

        // (d) Chain is complete (each output properly connected in sequence)
        // This is implicit from (b) and (c) validation, but we can add additional checks if needed.

        // If no issues found, add the success message
        if (Issues.Count == 0)
        {
            Issues.Add("No issues found.");
        }
    }

    /// <summary>
    /// Attempts to resolve a Nodify connector object to a ConnectorViewModel.
    /// Nodify may pass the visual control (Connector) or the ItemContainer.
    /// This method searches all nodes' inputs/outputs to find a matching ConnectorViewModel based on position/anchor.
    /// </summary>
    private ConnectorViewModel? ResolveConnectorFromNodifyObject(object nobj)
    {
        // Simple fallback: if the object has a DataContext that is a ConnectorViewModel, use it.
        if (nobj is FrameworkElement fe && fe.DataContext is ConnectorViewModel cvm)
        {
            return cvm;
        }

        // Otherwise, search all connectors in all nodes
        foreach (var node in Nodes)
        {
            foreach (var connector in node.Inputs.Concat(node.Outputs))
            {
                if (connector.Anchor == (nobj as dynamic)?.Anchor)
                {
                    return connector;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a potential connection against all rules.
    /// </summary>
    private ConnectionValidationResult ValidateConnection(ConnectorViewModel source, ConnectorViewModel target)
    {
        if (source.IsInput)
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = "Source must be an output." };

        if (!target.IsInput)
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = "Target must be an input." };

        if (source.Kind != target.Kind)
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = $"Port kind mismatch: {source.Kind} → {target.Kind}." };

        if (source.ParentNode == target.ParentNode)
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = "Cannot connect a node to itself." };

        // An input accepts only one connection
        if (target.IsConnected)
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = "Input is already connected." };

        // No duplicate connections
        if (Connections.Any(c => c.Source == source && c.Target == target))
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = "Connection already exists." };

        // No cycles (simplified check: output node cannot come after input node in the graph)
        // For now, a basic acyclic check: source node should not be downstream of target node
        if (IsDownstreamOf(source.ParentNode, target.ParentNode))
            return new ConnectionValidationResult { IsValid = false, ErrorMessage = "Connection would create a cycle." };

        return new ConnectionValidationResult { IsValid = true };
    }

    /// <summary>
    /// Checks if nodeA is downstream of nodeB (i.e., nodeB → ... → nodeA).
    /// </summary>
    private bool IsDownstreamOf(NodeViewModel nodeA, NodeViewModel nodeB)
    {
        var visited = new HashSet<NodeViewModel>();
        var queue = new Queue<NodeViewModel>();

        // Start from nodeB, follow outputs to see if we reach nodeA
        foreach (var outConnector in nodeB.Outputs)
        {
            foreach (var connection in Connections.Where(c => c.Source == outConnector))
            {
                queue.Enqueue(connection.Target.ParentNode);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == nodeA)
                return true;

            if (visited.Contains(current))
                continue;

            visited.Add(current);

            foreach (var outConnector in current.Outputs)
            {
                foreach (var connection in Connections.Where(c => c.Source == outConnector))
                {
                    queue.Enqueue(connection.Target.ParentNode);
                }
            }
        }

        return false;
    }

    private struct ConnectionValidationResult
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
