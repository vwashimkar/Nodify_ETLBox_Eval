# Project purpose
POC to EVALUATE two libraries: Nodify 7.3.0 (WPF node editor) and ETLBox 3.11.1 (ETLBox.Csv, ETLBox.Json).
Target: net10.0-windows, single WPF project, nullable enabled, file-scoped namespaces.

# Reference material (read-only, excluded from build)
- /reference/Nodify: Nodify library source, same version family as the 7.3.0 NuGet package.
- /reference/Nodify.Calculator: closest example to our editor (Node with typed NodeInput/NodeOutput).
- /reference/Nodify.StateMachine: control-flow style example, NOT the model for our data-flow editor.
When unsure of any Nodify member or behavior, read these BEFORE writing code. Quote the file you relied on. Do not copy example code wholesale; adapt it. Never modify /reference.

# Use case (the only scope)
1. CsvSource reads employee_list.csv (columns: id, first_name, last_name, email, age, location, salary, joined).
2. location is "Country|City". Filter rows by the country the user selects (split on first '|', trim, ordinal ignore-case).
3. Filtered rows (all columns) -> JSON string, shown in output pane 1 (indented).
4. That JSON is parsed back into List<Dictionary<string,string>> containing ONLY first_name, last_name, email.
5. The list is serialized to indented JSON and shown in output pane 2.
The pipeline is built visually with Nodify nodes and executed with ETLBox components.

# Allowed dependencies
Nodify, ETLBox, ETLBox.Csv, ETLBox.Json, and the .NET BCL (including System.Text.Json) only.
Do NOT add any other PackageReference without asking me first. No CommunityToolkit, no Microsoft.Extensions.*, no Newtonsoft, no CsvHelper.

# MVVM
Plain MVVM with hand-written ObservableObject (INotifyPropertyChanged + SetProperty) and RelayCommand (ICommand). No source generators, no DI container; compose objects manually in App.xaml.cs.

# Guard rails
- Use the real Nodify and ETLBox APIs directly. Do NOT create wrapper or abstraction layers around their types. Thin glue code only.
- Do NOT re-implement library features: no manual CSV splitting, no hand-written JSON parsing, no custom graph rendering or custom topological sort.
- Never invent API members. Verify every Nodify and ETLBox member against the referenced assemblies (Go To Definition / build). If something is missing or awkward, implement the simplest workaround and LOG it in EVALUATION.md.
- EVALUATION.md: append an entry whenever a library caused friction (library, what I tried, what went wrong, workaround, severity) and also notable positives.
- Scope lock: no persistence, undo/redo, extra node types, extra tabs, or features not in the use case above.
- ETLBox components are single-use: rebuild the whole network on every Run.
- ETLBox runs async: never block the UI thread; marshal UI updates through the Dispatcher.
- Treat all values as strings, use ordinal/invariant culture, never parse dates or numbers.
- No swallowed exceptions: errors must appear in the Issues pane with the node name.
- Small focused changes, one class per file, build and fix errors after every step. Do not refactor unrelated files.

# Roslyn
- Microsoft.CodeAnalysis.CSharp (Roslyn) is allowed ONLY for Stage 4 (DLL generation). Do not add or use it before that stage is requested.

- Microsoft.CodeAnalysis.CSharp (Roslyn) is allowed for Stage 4 only, and only inside Engine/Generation. Settings values must be embedded using Roslyn's literal escaping, never string concatenation. Never read, copy or embed etlbox.lic.