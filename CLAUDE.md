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
- Unit tests: `dotnet test Nodify_ETLBox_Eval.slnx` (currently 50 tests, all pass; add `--blame-hang-timeout 60s` so a hang cannot freeze the run)
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
- Next stages (see section 10 of the spec): 3 JsonCheckpoint node, 4 ParseValue (engine and unit tests first, test files C01 to C12, C15, C16), 5 .NET object destination, 6 Execute with progress and DLL generation for the new nodes, 7 save/open project.
- Parked: mixed instrument files (a text block followed by a CSV part), see section 12 of the spec. Not scheduled.
- Known untested: `PrepareHeaderForMatch`, duplicate header names (including a header named `#`, which the Data Studio table also uses), reading above 5,000 rows (needs a valid licence). Preview cancel is noticed only when the next row arrives, so a cancel can be slow on a slow share or a very long skipped preamble [U].
