# ETLBox 3.11.1 API Surface (Verified)

Evidence source:
- Runtime assemblies from `Nodify_ETLBox/bin/Debug/net10.0-windows`
- Reflection run on 2026-10-23
- Assemblies inspected: `ETLBox.dll`, `ETLBox.Csv.dll`, `ETLBox.Json.dll`, `ETLBox.Licensing.dll`

## CsvSource<T>
Type: `ETLBox.Csv.CsvSource<T>`

Constructors:
- `CsvSource()`
- `CsvSource(string uri)`
- `CsvSource(string uri, ResourceType resourceType)`

Declared public properties:
- `ETLBoxException BadDataException { get; set; }`
- `CsvHelper.Configuration.CsvConfiguration Configuration { get; set; }`
- `Action<CsvContext> CsvContext { get; set; }`
- `string[] FieldHeaders { get; set; }`

Declared public methods:
- `string[] ReadFieldHeaders(string uri)`

Inherited public members found by keyword scan (from ETLBox data flow base types):
- Methods: `Execute`, `ExecuteAsync`, `LinkErrorTo`
- Properties: `Encoding`, `ErrorCount`, `ErrorSource`, `OnProgress`, `ProgressCount`

Delimiter/header/encoding related verification:
- No public `Delimiter` property exists directly on `CsvSource<T>`.
- `CsvSource<T>.Configuration` exists and is typed as `CsvHelper.Configuration.CsvConfiguration`.
- Public properties on `CsvHelper.Configuration.CsvConfiguration` relevant to this task include:
  - `string Delimiter`
  - `bool DetectDelimiter`
  - `string[] DetectDelimiterValues`
  - `Encoding Encoding`
  - `bool HasHeaderRecord`
  - `HeaderValidated HeaderValidated`
  - `PrepareHeaderForMatch PrepareHeaderForMatch`

## RowTransformation
Type: `ETLBox.DataFlow.RowTransformation`

Constructors:
- `RowTransformation()`
- `RowTransformation(Func<object, object> rowTransformationFunc)`
- `RowTransformation(Func<object, object> rowTransformationFunc, Action initAction)`

Declared public properties:
- none

Declared public methods:
- none

Inherited public members found by keyword scan:
- Method: `LinkErrorTo`
- Properties: `ErrorCount`, `ErrorSource`, `OnProgress`, `ProgressCount`

## MemoryDestination<T>
Type: `ETLBox.DataFlow.MemoryDestination<T>`

Constructors:
- `MemoryDestination()`

Declared public properties:
- `bool ClearDataOnStart { get; set; }`
- `ICollection<T> Data { get; set; }`

Declared public methods:
- none

Inherited public members found by keyword scan:
- Method: `LinkErrorTo`
- Properties: `ErrorCount`, `ErrorSource`, `OnProgress`, `ProgressCount`

## JsonSource
Type: `ETLBox.Json.JsonSource`

Constructors:
- `JsonSource()`
- `JsonSource(string uri)`
- `JsonSource(string uri, ResourceType resourceType)`

Declared public properties:
- none

Declared public methods:
- none

Inherited public members found by keyword scan:
- Methods: `Execute`, `ExecuteAsync`, `LinkErrorTo`
- Properties: `Encoding`, `ErrorCount`, `ErrorSource`, `OnProgress`, `ProgressCount`

## JsonDestination
Type: `ETLBox.Json.JsonDestination`

Constructors:
- `JsonDestination()`
- `JsonDestination(string fileName)`

Declared public properties:
- none

Declared public methods:
- none

Inherited public members found by keyword scan:
- Method: `LinkErrorTo`
- Properties: `Encoding`, `ErrorCount`, `ErrorSource`, `OnProgress`, `ProgressCount`

## Network
Type: `ETLBox.DataFlow.Network`

Constructors:
- `Network()`
- `Network(IDataFlowComponent[] startNodes)`

Declared public properties:
- `ReadOnlyCollection<IDataFlowComponent> AllNodes { get; }`
- `bool? AllowMultipleExecutions { get; set; }`
- `ReadOnlyCollection<IDataFlowDestination<object>> Destinations { get; }`
- `bool IgnoreUnlinkedNodes { get; set; }`
- `Action OnCompletion { get; set; }`
- `Action<Exception, IDataFlowComponent> OnException { get; set; }`
- `Action OnInitialization { get; set; }`
- `ReadOnlyCollection<IDataFlowSource<object>> Sources { get; }`
- `ICollection<IDataFlowComponent> StartNodes { get; set; }`
- `bool TryAddComplementVoidLinks { get; set; }`

Declared public methods:
- `Network AddComplementVoidLinks()`
- `Network ApplyToAllNodes(Action<IDataFlowComponent> onNode)`
- `void Cancel()`
- `void Cancel(IDataFlowComponent[] startNodes)`
- `void Execute()`
- `void Execute(CancellationToken cancellationToken)`
- `void Execute(IDataFlowComponent[] startNodes)`
- `void Execute(CancellationToken cancellationToken, IDataFlowComponent[] startNodes)`
- `Task ExecuteAsync()`
- `Task ExecuteAsync(CancellationToken cancellationToken)`
- `Task ExecuteAsync(IDataFlowComponent[] startNodes)`
- `Task ExecuteAsync(CancellationToken cancellationToken, IDataFlowComponent[] startNodes)`
- `Network Init(IDataFlowComponent[] startNodes)`
- `Network Init()`
- `Network InitAndStartWith(IDataFlowSource<object> source)`
- `Network LinkAllErrorTo(IDataFlowDestination<object> errorDestination)`
- `Network LinkTo(IDataFlowDestination<object> destination)`
- `Task ParallelExecute(Network[] networks)`
- `string PrintAsJson(Func<IDataFlowComponent, object> propertyExtraction)`
- `string PrintAsJson()`
- `NetworkNode PrintAsNodes(Func<IDataFlowComponent, object> propertyExtraction)`
- `NetworkNode PrintAsNodes()`
- `Network SetLogInstance(ILogger logger)`
- `Network StartWith(IDataFlowSource<object> source)`

## LicenseService
Type: `ETLBox.Licensing.LicenseService`

Constructors:
- none (static service type)

Declared public properties:
- `string CurrentKey { get; set; }`

Declared public methods:
- `void Check(int progressCount, string featureName)`
- `void ResetLicenseInformation()`

## Explicit "does not exist" checks (public API)
- `CsvSource<T>.Delimiter` property: **not found**.
- `CsvSource<T>.Settings` property: **not found**.
- `RowTransformation` public `Execute*` methods: **not declared on this type** (execution handled via network/component base pipeline).
- `JsonSource`/`JsonDestination` public delimiter/header properties: **not found**.
