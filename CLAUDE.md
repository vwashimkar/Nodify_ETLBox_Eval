# CLAUDE.md

Instructions for Claude Code in this repository. Read this file, `docs/CSV_WORKFLOW_SPEC.md` and `docs/lessons-learned.md` before any work.

## What this repo is

`Nodify_ETLBox_Eval` is a WPF proof of concept (Visual Studio, net10.0-windows, root namespace `EtlDesigner`) that evaluates ONLY two libraries: Nodify 7.3.0 (node editor) and ETLBox 3.11.1 (ETLBox.Csv, ETLBox.Json). Nothing else may be added or evaluated.

It is a visual ETL designer for a lab technician working with instrument exports. The first POC (five nodes: CsvSource, FilterByCountry, ToJson, ParseJson, ResultJson) is finished and is called the "legacy chain". The current work is the CSV slice described in `docs/CSV_WORKFLOW_SPEC.md`.

The user is not a professional developer. Explain what you changed in plain words, in short sentences, and say what you could not verify.

## Layout

- Solution: `Nodify_ETLBox_Eval.slnx`
- App: `Nodify_ETLBox/` (project file `Nodify_ETLBox/Nodify_ETLBox.csproj`)
  - `Engine/` has no WPF types. All ETLBox calls live here. `Engine/Csv/` is the new CSV code. `Engine/Generation/` is the Roslyn DLL generator. The other `Engine/` files are legacy.
  - `UI/ViewModels/`, `UI/Services/`, `UI/Resources/NodeTemplates.xaml`, `UI/Themes/Tokens.Light.xaml` and `Tokens.Dark.xaml` (design tokens), `UI/DataStudioWindow.xaml`.
  - `SampleData/TestData/` (first POC test files) and `SampleData/TestData/composite/` (CSV slice test files, with `expected_results.csv`).
  - `Spike/` has the legacy test runner and `CsvProbe.cs`.
- Tests: `Nodify_ETLBox.Tests/` (xUnit).
- Docs: `docs/` (spec, lessons learned, `csv-probe-results.md`). `EVALUATION.md` is in the repo root.
- `reports/` is git-ignored and is where you write reports.

## Commands

Close any running copy of the app before building. A running copy locks the DLLs.

- Build: `dotnet build Nodify_ETLBox_Eval.slnx`
- Unit tests: `dotnet test Nodify_ETLBox_Eval.slnx` (currently 430 tests: 428 pass, 2 skipped (the two C10 full-file tests, need a valid licence); about 25 s because of the DLL tests; add `--blame-hang-timeout 60s` so a hang cannot freeze the run)
- Legacy test run: `dotnet run --project Nodify_ETLBox/Nodify_ETLBox.csproj -- --run-tests`
  - The baseline is **43 PASS / 5 FAIL / 4 OBSERVED** while the ETLBox trial licence is expired (the five failures are T048 to T052, with the message "The registered trial key expired"). With a valid licence the baseline is 48 / 0 / 4. Any other result means you changed something legacy: stop and report.
- CSV probe: `dotnet run --project Nodify_ETLBox/Nodify_ETLBox.csproj -- --probe-csv`

You cannot see or click the WPF window. Never claim a visual result. Say what you expect and list the manual checks for the user.

## Rules

Architecture
- `Engine/` has no reference to WPF. Roslyn is allowed only in `Engine/Generation/`.
- Preview and run read CSV through the same ETLBox code path (`Engine/Csv/CsvPreviewReader.cs`). Never write a second CSV reader for results. `CsvDialectGuesser` only suggests settings and never produces data.
- The composite-cell parser (later stage) is a pure function in `Engine/Parsing/` with unit tests. It must not assume a group size, contiguous columns or six components. The mapping of components to columns ("slots") is data.
- No logic in code-behind. Dialogs open through `IDialogService` and have their own view model.
- Node settings are described by a schema and forms are generated from it. Extend `SettingViewModel`; do not replace it.
- Every new Engine file used by a generated pipeline is added to the DLL generator's embedded list, and the DLL test run is repeated. The Generate DLL button must keep working, because the DLL is used in a different WPF app.
- Do not change the five legacy nodes, `Engine/PipelineCore.cs` or the legacy Run behaviour unless the task says so.
- Never pass a caller's CancellationToken to ETLBox ExecuteAsync; a cancel that lands while ETLBox starts makes it wait forever. Check the caller's token before starting and on every row, and cancel a token you own from the row step (see CsvPreviewReader).

Style
- Hand-written styles from design tokens in `UI/Themes/Tokens.*.xaml`. No literal colours, font sizes or spacing in views. Use the `Thickness` tokens for `Margin` and `Padding`; a number token cannot be used for them (this caused a crash once).
- `Padding` does not exist on `Grid`, `StackPanel`, `DockPanel` or `WrapPanel`. Wrap in a `Border`.
- Bind read-only view model properties with `Mode=OneWay`.
- Check every dialog in both themes. 8-pixel spacing grid. Segoe UI Variable for text, Cascadia Mono or Consolas for data. No emoji in the UI.
- Long work runs off the UI thread with a spinner, and a Cancel button after half a second.
- Error messages say what happened and what to do next. They never contain secrets or the licence key.
- Any handler that ends the app must first write the exception text to a log file and show it. The app once exited silently with code -1 and hid the real error.

ETLBox facts
- Section 0 of `docs/CSV_WORKFLOW_SPEC.md` lists what has been proven by probes. Facts about ETLBox that are not proven in this repo are marked unverified `[U]` in code comments and tests until a probe confirms them. Evidence tags: `[P]` proven, `[A]` API dump, `[D]` docs, `[U]` unverified.
- The free edition stops at 5,000 rows read per connector. Preview reads are at most 1,000 rows.

## Never do this

- Never read, print, copy, embed, commit or quote `etlbox.lic` or any licence key. It is git-ignored.
- Never edit `EVALUATION.md`, `README.md` or anything in `docs/` unless the task names the file.
- Never add a package, library or tool without asking.
- Never delete files outside what the task needs. Never run `git reset --hard`, `git clean`, force-push or rewrite history.
- Never push to a remote unless asked.

## How to work

- One stage per task. Read the stage's text in the spec first. If the task is ambiguous or contradicts the spec or the code, stop and ask one short question.
- Look at the code before changing it. Report the cause of a bug before you fix it.
- Make the smallest change that does the job. Do not refactor files the task does not mention.
- After a change: build, run `dotnet test`, run the legacy `--run-tests` and compare with the baseline. Report the exact numbers from the output. Do not summarise a number you did not see.
- Treat agent-written claims, including your own earlier reports, as unverified until checked against raw output. Earlier reports in this project contradicted the raw results more than once.
- Write each report to `reports/<name>-report.md` and keep the chat reply short: what changed, the test numbers, what you could not verify, and the manual checks the user should do.
- Work on the branch `csv-slice`. Commit when a stage builds and its tests pass, with a short message in plain words. The user pushes.

## Current state (update this section after each stage)

- Done: CsvFile node, validation, dialect guesser, dialog service, `CsvPreviewReader`, `CsvErrorText`, `DataStudioViewModel`, `CsvColumnSelection`, the real `DataStudioWindow`.
- Done 2026-11-03: the five open fixes from manual checks (`reports/fixes-report.md`): read-only bindings `Mode=OneWay`, node colours from tokens in both themes, Issues list rebuilt on each validation, Data Studio theming, editable Encoding list, Separator preset names. Not yet checked by eye.
- Done 2026-11-03 (`reports/taskA-report.md`): the preview hang is fixed. A caller's cancel that landed while ETLBox started the network made `ExecuteAsync` wait for ever; the caller's token is no longer passed to ETLBox. `CsvPreview.Cancelled` reports a cancel (no rows, no error). A cancelled reload in Data Studio keeps the previous data, and only the newest reload stops the spinner. Every test that awaits ETLBox goes through `TestTimeout.Run` (10 s). `.editorconfig` sets UTF-8. `InternalsVisibleTo` for the tests is in the project file; no Engine file has assembly attributes.
- Done 2026-11-03 (`reports/taskB-report.md`): ParseValue engine in `Engine/Parsing/` (`ParseValueSettings`, `CompositeCellParser`, `ParseValueTransform`), long shape only, no UI. Manifest test passes every non-JSON row (C01 to C09, C11, C11b, C12, C14); C10 skipped (needs a valid licence and a full-file reader); C15 and C16 belong to the JSON stage. `dotnet test`: 107 passed, 1 skipped, 108 total (the "50 tests" in Commands is out of date). Open questions: Wide shape is not defined in the spec; the reason for a key without components ("no components") is chosen, not specified.
- Done 2026-11-03 (`reports/taskC1-report.md`): ParseValue node (palette, one CsvFile input, settings summary, placeholder Configure window), connection rules, validation (`Engine/Parsing/ParseValueValidator.cs`), upstream resolver (`UI/Services/ParseValueUpstreamResolver.cs`, columns cached on the CsvFile node as `KnownColumns`), `Engine/Pipeline/SettingsJson.cs`, `ParseStudioViewModel` (no real dialog yet). `CsvColumnSelection` moved to `Engine/Csv/`. Not yet checked by eye.
- Done 2026-11-04 (`reports/taskC2-report.md`): palette drag added two nodes (the button's Click fired after the drop), fixed in `MainWindow.xaml.cs`. One validator message when no slot has columns. `HighlightSegments`. The real ParseValue dialog bound to `ParseStudioViewModel`, with `UI/Controls/SampleGridBehavior.cs`; dialog styles shared in `UI/Themes/DialogStyles.xaml`. Not yet checked by eye.
- Done 2026-11-04 (`reports/taskD-report.md`): Task D, the runnable workflow CsvFile -> ParseValue -> DotNetObject. `Engine/Pipeline/` has `PipelineDefinition` (+ validator, JSON), `OutputSettings` (`ObjectKinds`: only `List<Dictionary<string,string>>` works) and `PipelineRunner` (full file, progress every 250 ms, rejects, flagged, licence stop as one message with the rows kept). `Engine/Csv/CsvRecordReader.cs` is the one ETLBox CSV path; the preview reads through it. `ParseValueStep` processes one record. DotNetObject node (`SettingKind.Choice`), its connection rules and Issues. Execute button (tooltip says why it is disabled) and `UI/ExecuteWindow.xaml` with `ExecuteViewModel`. New Engine files are NOT yet in the DLL generator (Task E). Without a valid licence ETLBox delivers at most record 5,001, then stops [P]; the count at the stop varies, see the note on the licence stop below. Not yet checked by eye.
- Done 2026-11-04 (`reports/taskE-report.md`): Task E, Generate DLL for CsvFile -> ParseValue -> DotNetObject. `Engine/Generation/CsvPipelineDllGenerator.cs` embeds 13 Engine files (its own list), makes them internal, and adds a public wrapper (entry class with `RunAsync`, `DefinitionJson`, `OutputColumns`; `PipelineRunResult`, `RejectInfo`, `PipelineRunProgress`) plus `README_usage.txt`. It reuses the legacy compile step (`DllGenerator.Compile`). The Generate DLL button opens `UI/GenerateDllWindow.xaml` for the new graph only after a successful Execute of the same settings (SHA-256 of the definition JSON); the legacy rule is unchanged. App and DLL results are equal for every manifest row. `Consumer/CsvPipelineConsumer/` is a console app outside the solution that uses the DLL (see `Consumer/README.md`). Not yet checked by eye.
- Done 2026-11-06 (`reports/taskF-report.md`): Task F, save and open projects. `Engine/Project/` has the `.etlproj` model (`ProjectFile`, `ProjectSerializer` with `formatVersion` and the `Migrate` hook, `ProjectNodeSettings`, `ProjectPaths`, `ProjectFileStore` with the safe save and `.bak`, `IRecentProjects`). `EditorViewModel.Project.cs` has `ToProject`, `FromProject` and `IsDirty`; `MainViewModel.Project.cs` has New, Open, Save, Save As, Recent, the title and the unsaved-changes flow. A loaded connection goes through `TryConnect`, the same rules as the mouse. A node of an unknown type is a `NodeType.Unknown` placeholder that keeps its raw JSON; a connection that cannot be restored stays in the file with an Issues line. Not saved: preview rows, results, last successful run, known columns, theme, window size. Sample: `SampleData/Projects/c01_pipeline.etlproj` (regenerate with `ETLPROJ_WRITE_SAMPLE=1`). Not yet checked by eye.
- Done 2026-11-06 (`reports/taskP1-report.md`): Task P1, four fixes. `Engine/Generation/OutputFolderCheck.cs` accepts a folder that does not exist yet and has one message per refusal. Auto-fill needs a grid selection (no all-columns fallback). Run on a graph with only new-style nodes shows one line pointing to Execute. Data Studio clamps Preview rows to 1,000 (`CsvPreviewReader.MaxPreviewRows`, also enforced in the reader), shows the format hint only when `CsvPreview.IsFormatError`, and a failed load clears the grid and status but keeps the column selection. Not yet checked by eye.
- Next stages (see section 10 of the spec): 3 JsonCheckpoint node; from stage 7 undo/redo and empty states are still open.
- Parked: mixed instrument files (a text block followed by a CSV part), see section 12 of the spec. Not scheduled.
- Licence stop row count (found 2026-11-06, `reports/taskP1-report.md`, re-check section): without a valid licence a run can stop well before 5,000 rows, and the count differs each time (4,835 and 285 captured). Rows waiting in ETLBox's buffer are probably dropped at the stop [U]. The tests `C10_WithoutAValidLicence_GivesTheLicenceMessage_AndKeepsTheRows` and `Cancel_Repeated20Times_ReturnsQuickly_AndReleasesTheFile` now accept any count up to 5,001, so this no longer fails them. Only the tests changed, not the run code.
- Known flaky, not fixed: `ParseValueNodeTests.ConfigureCommand_CallsTheHandlerWithTheNode` (failed once on 2026-11-06 and once on 2026-11-07, `opened` was null; `NodeFactory.ParseValueConfigureHandler` is static and `MainViewModel` sets it, so another test class running at the same time probably overwrites it [U]). Not yet failed but same risk: `ExecuteViewModelTests.C10_WithoutAValidLicence_ShowsTheLicenceMessage_AndKeepsRows` expects 200 shown rows, so a very early licence stop could break it [U].
- Fixed 2026-11-07: `ProjectEditorTests` cleanup could not delete `in.csv`. `MainViewModel` reads a new graph's columns in the background and no test waits for that read. Cleanup now retries the delete for up to 5 s. Only the test changed. The app's read is still not awaited, and `SampleProjectTests` has the same cleanup without the retry, not checked.
- Known untested: `PrepareHeaderForMatch`, duplicate header names (including a header named `#`, which the Data Studio table also uses), reading above 5,000 rows (needs a valid licence). Preview cancel is noticed only when the next row arrives, so a cancel can be slow on a slow share or a very long skipped preamble [U].


# Plan: Intake stage (mixed-format files from instruments), v0.2

Status: draft 0.2 for the user's review. Nothing here is built. When you approve it, copy it to `docs/INTAKE_PLAN.md` and run the stage prompts one at a time, as before.

## 0. What changed since v0.1 (the user's decisions)

- **Real instrument files have NO markers.** The `##### BEGIN` file is a synthetic stress test. So **auto-detection is the core** of this stage and marker reading is a second, simpler mode (it is also our ground truth: strip the markers from the stress file and the detector must find the same blocks).
- One node per container. A later option "all containers of format X" is allowed. After Execute, containers not connected to anything are listed ("3 containers not used").
- Provenance columns are off by default. Node names come from a text file. Folder watching is postponed.
- **YamlDotNet is approved** (YAML code only). JSON and XML use the .NET base library. SOAP is read as XML. BadgerFish is our own small convention layer.
- Temp files for segments are accepted.
- The design must allow for **DMBOK 2.0 direction**: the original input is archived, with lineage, in a local SQLite database that later syncs to a central database. Configurable after the POC is approved. This plan only prepares for it (section 10); it builds no SQLite now.
- Order of work: Task F (save and open project) first, then this stage.

## 1. What the user asked for

1. The user gives the tool a file (a folder or other source later).
2. One file can hold many formats. When the read is complete the tool splits it into **containers** (segments), classifies each one, and shows them **in the order they occur in the file**.
3. A **progress bar** shows the reading.
4. Each container has **outbound connections** to the components we have (CSV -> ParseValue -> .NET object, JSON checkpoint later) or will build.

## 2. What the video showed

Alteryx One loaded the sample as ONE table: 218 rows, one string column `column1`, text split on commas into `column2`, `column3` ..., JSON, XML, CSV and prose mixed in one grid, and "Data Insights" such as "Number of column1 = 170". Nothing was recognised. That is the baseline to beat: for the sample, 18 typed containers plus 2 text blocks, in order, each with its own preview, found with and without markers.

## 3. Concept

```
 [FileIntake] --> [Container: metadata header] (keyvalue) --> [DotNetObject]
      |       --> [Container: results table]   (csv)      --> [ParseValue] --> [DotNetObject]
      |       --> [Container: footer]          (text)     --> [DotNetObject (text)]
      '-- Intake Studio: container map, previews, progress, boundary editing, "Save layout"
```

- **FileIntake node**: file path (Browse or drag and drop). It reads and splits the file. One output, "Containers".
- **Container node**: one container, chosen by its order and format (and name when there is one). Its output port is typed: Rows (csv, tsv), Json, Xml, Text, KeyValue. Wrong types cannot connect.
- **Intake Studio** (modal dialog, same shared styles and tokens as Data Studio) with a **Containers panel**: containers grouped by format as the user described (CSV (2), JSON (3), Text (2) ...) and listed in file order. Actions per container: for CSV/TSV "Send to ParseValue" (creates the nodes, connects them, opens Data Studio); for text and formats with no node yet "Send to .NET object (raw text)"; for all, "Create nodes for all containers" in lanes by format.
- **Layout template**: once the user is happy with the split, "Save layout" stores it so the next export from the same instrument is split the same way (section 6). This is what makes the generated DLL deterministic.

## 4. Auto-detection (the core)

The detector reads the file once as a stream of lines (never the whole file in memory), builds **regions**, and gives each region a format and a confidence (Sure, Likely, Guessed). Rules, in this order of priority:

1. **JSON**: a region starting at a line whose first non-blank character is `{` or `[` runs to the line where the string-aware brace/bracket depth returns to 0. Accepted only if System.Text.Json parses that span. Blank lines inside do not split it.
2. **JSON lines**: 2 or more consecutive lines that are each a complete JSON object.
3. **XML**: starts with `<?xml` or `<name`; ends at the closing tag of the root (read with XmlReader, DTD processing prohibited). Sub-type **SOAP** when the root is an Envelope in the SOAP 1.1 or 1.2 namespace. Sub-type **BadgerFish** (for JSON) is only a Guessed hint from `@` and `$` keys.
4. **Table (csv or tsv)**: consecutive lines that have the same field count, counted quote-aware across lines (a quoted field may span lines), for the best of the candidate delimiters `,` `;` `|` tab (best = most consistent count, fewest ambiguous quotes; decimal commas must not beat a `;` that is consistent). At least 3 lines, or 2 lines with 3 or more fields. A **blank line ends a table**, so two tables with the same layout separated by a blank line are two containers. Header guess: first row has no numeric field while the next rows do (shown as Guessed; the user decides). **Preamble lines** directly before a table (for example `# Report ...`) are offered as skip lines of that table. Footer rows such as `TOTAL` stay inside the table.
5. **Key-value**: 3 or more consecutive lines of the form `Name: value` with no nesting. This is the common instrument metadata header. It is not YAML; it becomes a one-row table of metadata that can later be carried onto the rows of a table (planned, section 11).
6. **YAML**: only when there is real YAML structure (nesting by indentation, `- ` lists, anchors, `|` or `>` blocks) AND YamlDotNet parses the region. Otherwise key-value or text.
7. **Text**: everything else. Adjacent text lines form one block; a single blank line does not split text, two or more blank lines do [U].

Honest limits (tests say so, see section 13): some splits are ambiguous by nature. Prose that looks like `key: value` lines, YAML with a blank line inside, a table preamble, and two tables with the same layout and NO blank line between them cannot always be split correctly. The tool therefore (a) shows confidence, (b) never guesses silently, (c) lets the user fix any boundary, and (d) learns the layout (section 6). Detector thresholds are data in settings, not code constants.

## 5. Marker mode (secondary)

Kept because it is cheap, exact, and the ground truth for testing. Default profile `##### BEGIN <format> | <name> | <notes> #####` / `##### END #####`, column 0, case-sensitive, trailing blanks allowed; an indented or lower-case line, or `<format>` that is not a name, is text. Unknown format name -> text with a Warning. Missing name -> `<format>-<n>`. Content is the lines strictly between the markers, never trimmed. Problems never stop the scan: BEGIN not closed before the next BEGIN -> closed there, Warning; BEGIN with no END at end of file -> runs to the end, Error; END without BEGIN -> kept as text plus a file-level warning; empty segment -> Warning. Text blocks are the gaps between segments (blank edge lines dropped). The full contract, with every case, is in `intake_test_data.zip` (`README.md` and `expected/*_segments.csv`). Mode setting on the node: Auto-detect (default), Markers, Markers then Auto-detect for unmarked parts.

## 6. Layout template (saved per instrument)

After the user confirms or edits the split in Intake Studio, "Save layout as..." stores an ordered list of region rules in a JSON file in the config folder (later also in SQLite):
- for each region: kind (csv, tsv, json, keyvalue, text, ...), name, how it **starts** (an anchor: the first line matching a text or pattern, the first line with N fields, or "right after the previous region"), how it **ends** (blank line, end of file, a footer pattern, or a fixed number of lines), and its options (delimiter, header yes/no, skip lines).
- Applying a template to a new file is deterministic. If the file does not fit (an anchor is missing, a table has a different field count) the result says **"Layout mismatch"** with the reason and which region, and offers Auto-detect as a fallback. It never splits silently differently.
- The template is part of the pipeline definition, so Execute and the generated DLL use the same split. In the DLL a mismatch comes back as a warning in the result.

## 7. Review in Intake Studio (human in the loop)

- Container map: order, format chip (colour plus text), name, line range, size, confidence, status. Filter chips by format with counts.
- Preview of the selected container: raw text with the original line numbers (first 500 lines), table grid for csv/tsv, pretty JSON, indented XML.
- Fix tools: **Split here** (at a line), **Merge with next**, **Change format**, **Rename**, **Set delimiter, header, skip lines**. Every edit updates the template. **Undo** for the session's edits.
- Empty states: no file; "No structure found: the whole file is text"; the problem list.

## 8. Progress bar

The reader streams the file in blocks through a counting stream: progress = bytes read / file size, reported about every 250 ms and at the end, never per line on the UI thread. Phases: Reading, Detecting, Validating (n of m containers), Done. Cancel appears after 0.5 s, is checked on every line, releases the file at once (tested by deleting the file after a cancel). Shown: containers found so far, per-format counts, elapsed time. Summary: file name, size, SHA-256 (computed in the same pass), encoding, BOM, line ending kind, line count, counts per format, warnings, elapsed ms.

## 9. Sources: Abstract Factory and the IntakeItem

- Every source delivers an **IntakeItem**: a stream plus provenance (source kind, name, size, received time in UTC, an id). The reader works on the item, not on a path. A source that cannot seek (TCP, an IoT subscriber) is **spooled to a temp file** first, so all later code works on a file with a known size and offsets.
- `IIntakeFactory` creates the family for one source kind: the source (delivers items), the trigger (when), the settings schema (so forms are generated, as for nodes) and the validator. A registry maps the kind to its factory. Built now: File only (plus the registry and tests). Later: Folder watch, TCP port monitor, IoT/MQTT subscriber (each its own task; each may need a package, which will be asked about then). Streaming sources need a rule for what one item is (a message, a time window, a size); decided when we get there.
- Temp files for containers: `%TEMP%\EtlDesigner\containers\<file hash>\<order>.<ext>`, removed on exit. One CSV code path stays (CLAUDE.md rule): `CsvRecordReader` keeps reading files. A probe (G0) checks whether ETLBox can read a stream instead.

## 10. Raw archive and lineage (DMBOK 2.0 direction; design now, build later)

Facts to keep true from the first line of code, so the later SQLite and sync work needs no redesign:
- **Originals are never modified.** Every intake has an id (GUID), UTC timestamps, the SHA-256, the tool version, the settings and layout-template hashes.
- `IRawArchive` (interface): `Store(IntakeItem) -> ArchiveId`, `Open(ArchiveId)`, keyed by hash so the same file is stored once. First implementation (G9, optional): a folder archive with a manifest JSON. Later: SQLite (blob or file reference, compressed), synchronised to a central database by an **outbox** (rows marked "pending sync", idempotent by id and hash). The choice of SQLite package and of encryption at rest is made when that task starts.
- `ILineageLog` (interface): one record per run: input id and hash, the containers found, which layout template and pipeline definition (by hash) produced which output, counts (read, parsed, rejected, flagged), rejects, start and end time, user, tool version. First implementation: a JSON file per run. This is the metadata DMBOK asks for (lineage, data quality measures, auditability).
- Configuration is read through `IConfigStore` (first implementation: text and JSON files in a config folder; later SQLite). It holds node labels, marker presets, detector thresholds and layout templates.
- Out of scope for now and for the user to decide later: retention rules, access control, encryption of the local archive, personal-data handling, who may change a layout template (approval workflow). Not building compliance, only not blocking it.

## 11. Connecting containers to what exists

- **CSV and TSV**: a `CsvContainer` source feeds ParseValue like CsvFile does now. Data Studio works on the container (preview 1,000 rows, ignore or keep columns, delimiter and header from the container rules).
- **Key-value metadata**: output is a one-row table. Later option on a CSV branch: "Carry header values" adds chosen metadata (Instrument, Operator, Date ...) as columns on every output row. This is likely very useful for lab files; it is proposed, not scheduled.
- **JSON and BadgerFish**: the planned JSON checkpoint node. BadgerFish gets an optional normalise step. **XML and SOAP**: a node that picks the repeating record element and flattens it; SOAP can unwrap the Body or Fault. **YAML**: a node that turns it into a JSON-like tree (YamlDotNet). **Text**: straight to a DotNetObject that gives a string. Every container has a place to go from day one (raw text out); the typed nodes arrive one per task.
- **Coverage report** after Execute: "N containers not used: ..." in the summary, so nothing is silently dropped.
- **Provenance columns** (`source_file`, `container_name`, `container_order`, `record_number`): one switch on DotNetObject, off by default; the default can change by configuration later.
- Rebinding after the file changes: a container node stores its order, format and (if present) name, and a short fingerprint of its first lines. If the new analysis cannot match it, the Issues tab says "Container moved or missing" instead of guessing.

## 12. Execute and the DLL

- `PipelineDefinition` gets `Intake` (mode, thresholds, layout template) and `Branches` (one per connected container node, each a chain ending in a DotNetObject). The old single chain is the case "one branch, source = a file", so the existing tests, Execute and the DLL keep working.
- Execute: read and split (with progress bar), run each branch (progress per branch), then show a tab per branch (output, rejects, flagged) plus a Containers tab (the intake summary, warnings, unused containers).
- Generated DLL: `RunAsync(path, progress, token)` returns one result per branch and the intake summary (containers found, layout mismatch warnings). The layout template and branches are baked as readable JSON, as now. YamlDotNet is listed in the DLL's README only when a YAML branch exists. App-versus-DLL parity tests are extended to the stress file, the marker-less files and the variants.
- The Generate DLL gate is unchanged: a successful Execute for the current settings.

## 13. Stages

| Stage | Scope | Done when |
|---|---|---|
| F | Save and open project (before this stage) | Versioned JSON project file; unknown node types tolerated |
| G0 | Probes: ETLBox CsvSource and JsonSource from a stream; speed of a byte-level line scan on a 130 MB file; quote-aware field counting cost | Results recorded as [P] or [U] |
| G1 | Foundation, no UI: `IntakeItem`, streaming line indexer (offsets, CR/LF/CRLF, BOM, SHA-256 in one pass, progress, cancel, re-read by offsets), lineage fields, marker mode | i01 to i07 match their expected CSVs exactly; large-file test; cancel releases the file; no hang |
| G2 | Auto-detect regions (section 4), thresholds as data | Every **Tier A** row of i09 and i10 found with the right format and exact lines; Tier B reported; i08 detections right; a false-positive test on plain prose files |
| G3 | Validation per container; YamlDotNet added for the YAML check (CLAUDE.md rule updated); SOAP and BadgerFish sub-types | Valid and invalid samples give clear messages |
| G4 | FileIntake node, settings JSON, `IConfigStore` with `labels.txt`, Intake Studio (map, previews, progress, cancel) | Manual checks, both themes, XAML loads without binding errors |
| G5 | Review tools (split, merge, change format) and layout templates; template applied to a new file with mismatch warnings | A template saved from i10 splits a second, different i10-style file correctly and warns on a file that does not fit |
| G6 | Container nodes, typed ports and rules, "Send to ParseValue", CsvContainer, Execute for one branch | The csv table of i10 runs to a .NET object with the same result as the same table as a standalone CSV file |
| G7 | Branches in `PipelineDefinition`, Execute tab per branch, coverage report, DLL with per-branch results, parity tests | App and DLL identical on i01, i09, i10 and the variants |
| G8a..e | One task each: JSON and BadgerFish, XML, SOAP unwrap, YAML tree, text; key-value carry columns | Each with its own expected files |
| G9 | `IRawArchive` and `ILineageLog` first implementations (folders and JSON); factory registry for more sources | Interfaces, tests, off by default |
| later | Folder watch, SQLite (config, archive, outbox sync), TCP port, IoT subscriber | Own plan and package OK each |

Visible wins first: G1 to G4 give the container map and the progress bar; G6 and G7 make containers flow to a .NET object and the DLL.

## 14. Tests

- Ground truth (`intake_test_data.zip`): i01 the stress file (18 marker segments plus 2 text blocks), i02 CRLF, i03 BOM, i04 to i07 marker problems and edge cases, i08 six single-format files without markers, **i09 the stress file with the markers removed** and **i10 an instrument-style file** (20 `Key: value` lines, a CSV table, 2 footer lines), each with an expected file made by a separate reference scanner, not by the code under test.
- **Tiers** for auto-detect: Tier A rows must be right (format and exact lines) and fail the build; Tier B rows are best effort and are REPORTED (right, merged, split, wrong format) without failing, each with the reason it is hard. Report the Tier B scorecard in every detector task.
- Large file generated at test time (the stress file repeated N times: 19N + 1 containers in marker mode): N = 2,000 (13 MB) and N = 20,000 (130 MB, marked Slow, run once and report time and memory). Memory must not grow with N.
- Property check: joining all containers and markers back in order reproduces the file's lines (nothing lost or duplicated).
- Cancel tests repeat 20 times on temp copies; everything slow uses `TestTimeout`; ETLBox rules in CLAUDE.md apply to the CSV reads in G6.
- **Real files**: the user supplies 3 to 5 real instrument exports (private values replaced, and which software makes them). They are added as Tier A cases, and the detector rules are expected to change after seeing them. This is the main risk of the stage, so G2 ends with a review of the detector on those files.

## 15. Risks

- Marker-less splitting is heuristic. Mitigations: confidence labels, human review, saved layout templates, mismatch warnings, thresholds as data, real files as tests.
- Real instruments may produce layouts our rules do not anticipate (several tables with different field counts and no blank lines, fixed-width columns, binary parts). The first real files decide what G2 must add.
- Very large containers: previews read only the first lines; branches stream.
- ETLBox free edition stops at 5,000 rows read per connector, per container read [U].
- Scope creep: one format branch per task; nothing is parsed that no node uses yet.

---

# Additions for CLAUDE.md (the user adds these when starting this stage)

- Packages: BCL types (System.Text.Json, System.Xml.Linq, FileSystemWatcher) are allowed. YamlDotNet is approved for YAML code only, from stage G3. Any other package needs an explicit OK.
- Originals are never modified. Every intake gets an id, UTC timestamps and a SHA-256. Code reads the original through an `IntakeItem`, never by writing to it.
- Configuration (labels, presets, thresholds, layout templates) is read through `IConfigStore`; no new hard-coded constants for them.
- Stage names: see docs/INTAKE_PLAN.md.

---
