# TASK G6c: the CSV part node on the canvas (branch g6)

Read first: CLAUDE.md, `reports/taskG6-design.md` (the accepted design, option A), `reports/taskG6b-report.md` (section "API for G6c and G6d").
Standing rules: do not read, print, embed or copy `etlbox.lic` or any licence key. No new package. Do not change the detector rules,
`PipelineCore`, the legacy nodes, or anything under `SampleData/TestData/intake/` (read-only; work on temp copies).
Free memory must be 4 GB or more before each full run. `dotnet build-server shutdown` first. One test process at a time.
No retry loops, sleeps or skips to hide a failing test: report the full message.

Scope: the canvas, the project file, the Execute chain and Generate DLL for a pipeline that starts with a CSV part.
NOT in scope: creating the selector from Intake Studio ("Use in pipeline") and the settings dialog. That is G6d.

## Step 0: three things G6b left open (do these first, small commits)

0a. **Delimiter detection in the part read.** ETLBox's `DetectDelimiter` is on by default, so a table whose first lines mislead
the detector can be read with another delimiter than the selector says. Turn it off only in `CsvRecordReader.Part.cs`
(the part overload). Do not touch the path overload (CsvFile behaviour must not change). Probe first with real ETLBox: a part whose
title lines contain many commas but whose table is semicolon- or tab-separated; show it was read wrong before and right after.
Add that test, plus one that proves the CsvFile path is unchanged.

0b. **Design-time columns must equal run columns.** G6b found that the selector drops trailing empty fields (r10 parts 4 and 6:
8 read / 7 expected; r11: 26 read / 10 expected) and writes `''` where ETLBox writes `Col<N>`. For ParseValue and Data Studio the
columns shown at design time must be exactly the columns the run produces. Rule: the canvas takes its columns from the same reader as
the run (`CsvPartLocator.LocateAsync`, then `CsvRecordReader.ReadAsync(location.Part!, ...)`), never from `location.ColumnNames`.
Add a test on r04 part 2, r10 part 4 and r11 part 1 (temp copies) that design-time columns == the columns of a run, in order.

0c. **Real files through the DLL.** Only i10 and one generated TSV went through a generated DLL. Add parity tests (app vs DLL, rows,
columns and facts) for r04 part 2 (16 columns) and r10 part 2, on temp copies. If one differs, stop and report the numbers.
Also reconcile the G6b test count: the report lists 7 + 26 new DLL tests (33) but the step 2 and 3 full run grew by 30, and the
test file is called `CsvPartDllTests.cs` in one table and `CsvPartDllGenerationTests` in the text. Say which is right and why.

## Step 1: node type, ports, palette

- `NodeType.CsvPart` ("CSV part"), shown in the palette next to File intake, same theme tokens as the other nodes (Light and Dark).
- File intake gets an output port. Rules: CSV part accepts input only from File intake; File intake may feed only CSV part (for now);
  CSV part outputs to ParseValue; one CSV part per pipeline; one File intake per CSV part. A refused connection shows the same kind of
  message as the existing connection rules.
- Existing projects and CsvFile pipelines open and run as before (byte-identical project JSON for projects without a CSV part).

## Step 2: node data, project file

- CSV part node holds the selector as the JSON from `CsvPartSelectorJson.Serialize` (a string field in the .etlproj, like `layoutPlan`),
  plus nothing else that is derived. Read/write round trip; a corrupt or missing selector opens the project without crashing and gives
  an Issues line, never a silent drop.
- The node shows (read-only): "Part N of the file", template name, expected delimiter, column count, status line. No editing in G6c.
- Creating one for now: tests build it in code, and add a small project fixture `SampleData/TestData/intake/projects/`-NOT: put it in
  `Nodify_ETLBox.Tests/TestData/g6c_i10_part.etlproj` (copy of i10 as the example file, selector from `FromSession`) so the user can open it.

## Step 3: design-time columns, Execute, Issues, Generate DLL

- Background refresh like CsvFile nodes: locate the part in the File intake's file, read the columns (step 0b rule), configure
  ParseValue. Failure of the locator shows its exact sentence on the node and in Issues (the G6a/G6b messages, unchanged).
- Execute: `MainViewModel.TryBuildExecuteDefinition` builds `PipelineDefinition.ForCsvPart(fileIntakePath, selector, ...)` and passes the
  graph chain to the validator. The result panel shows the part facts (file name, template, similarity, part number, lines).
- Issues list: new lines for missing File intake, missing selector, two CSV parts, CSV part not connected to ParseValue,
  File intake file missing. Same style as the existing lines.
- Generate DLL: the gate hash already covers csvPart; prove that changing the selector or the part invalidates a previously generated
  DLL status. Output columns still come from the Execute run. The generated DLL equals the app (reuse the G6b parity helpers on one
  canvas-built pipeline).

## Step 4: tests, screens, proof

- Tests: connection rules (every allowed and refused pair), project round trip with and without a CSV part, corrupt selector,
  refresh and Execute on i10 (rows equal the standalone CSV of the same part), every locator failure sentence on the node,
  two CSV parts, DLL generation from the canvas pipeline. WpfHost (STA), binding trace must be empty, token styles only.
- Screenshots with `INTAKE_SCREENS=1`: canvas with File intake -> CSV part -> ParseValue -> DotNetObject in Light and Dark at 1100x720,
  and the node in an error state. Put them in `reports/g6c/`. Look at them yourself and say what you see (clipping, empty areas).
- Tests must never write `lineage.log` or a `templates` folder next to the test exe.
- Three clean full runs in a row, legacy `--run-tests` counted from SpikeResults.csv (baseline 43 PASS / 5 FAIL T048-T052 / 4 OBSERVED).
- CLAUDE.md "Current state" line. Write `reports/taskG6c-report.md`: what changed per file, test numbers with free memory and duration
  per run, what you could not verify, and a section "For G6d" (what the Studio needs to call to create the selector and the node).
- Commit per step. Do not commit files you did not change.
