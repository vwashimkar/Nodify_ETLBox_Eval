# Evaluation: Nodify 7.3.0 and ETLBox 3.11.1

Proof of concept: a visual ETL designer in WPF (net10.0-windows). A CSV is filtered by country, turned into JSON, parsed back to `first_name`, `last_name`, `email`, and shown in two panes. The graph is built with Nodify and executed with ETLBox. Only these two libraries (plus ETLBox.Csv, ETLBox.Json and the .NET BCL) are being evaluated.

Last revised: 2026-10-24. Status: Stage 1 (spike and test suite) and Stage 2 (graph run) accepted (section 8). Stage 3 (country dropdown, node states, test-runner columns) implemented; the test-runner part is verified from `SpikeResults.csv`, the UI checks are reported as passing by the user (section 9). Stage 4 (DLL generation) accepted (section 10).

## How to read this document

Every claim carries an evidence tag. A claim without a tag does not belong here.

| Tag | Meaning |
|---|---|
| **[S]** | Result in `SpikeResults.csv` (52 tests, trial license present). The latest file was produced after Stage 3; where it differs from an earlier run, both are stated |
| **[M]** | Test manifest or README in `SampleData/TestData/` (inputs, ideal results, the spec) |
| **[A]** | `docs/etlbox-api-surface.md`: public API found by reflection on the shipped assemblies. It lists member names found by a keyword scan; behaviour is NOT tested by it |
| **[R]** | Reported in earlier session notes or screenshots, not re-checked for this revision |
| **[C]** | Read in our own source code by the reviewer (files supplied by the user, 2026-10-24, after the Stage 4a refactor) |
| **[U]** | Unverified. Stated only so it is not forgotten |

Findings are split into library behaviour (sections 3 and 4), defects in our own code (section 5) and our own design limits (section 6). Our mistakes are not library findings.

## 1. Scope and versions

- Target: net10.0-windows, WPF, plain MVVM (no CommunityToolkit, no DI).
- Nodify 7.3.0. ETLBox, ETLBox.Csv, ETLBox.Json 3.11.1.
- Spec for the test suite is in `SampleData/TestData/README.md` [M]. Key rules: country is the text before the first `|`, trimmed; comparison is ordinal and ignores case; equality only (`Indiana` does not match `India`); accents matter; a row with no `|` or an empty country is malformed and counted; every value is a string.

## 2. Result summary [S]

52 tests, trial license present: **48 PASS, 0 FAIL, 4 OBSERVED.**

- `strict` tests compare counts, row ids (SHA-1 of the comma-joined ids) and values against expectations computed with Python's `csv` module, not with the pipeline [M].
- `error_expected` tests pass when the run stops with a message. They do not check that the message names the true cause (see T038 in section 5).
- The latest run (after Stage 2) gives the same 48 / 0 / 4 as the run before it. The only differences are T038's message and T047's skipped count (section 5).
- The latest run (after Stage 3) is identical to the one after Stage 2 in every result field. The only differences are the error text now recorded for T041 and five new count columns: `rows_read`, `rows_matched`, `rows_not_matching`, `rows_skipped`, `reconciles` [S].
- `observe` tests have no pass condition. The four below record actual behaviour against the ideal result.

| Test | File | Ideal [M] | Actual [S] | Error shown? |
|---|---|---|---|---|
| T030 | 06_encoding_windows1252.csv (México) | 2 rows | 0 filtered, 0 skipped. rows_read 8, all counted as not matching | No |
| T040 | 12_ragged_rows.csv (India) | 3 rows (ids 1, 2, 5) | 4 filtered, 2 skipped. SHA-1 of the returned ids equals SHA-1 of `1,2,4,5`, so the 10-field row 4 was accepted. rows_read 7 = 4 matched + 1 not matching + 2 skipped | No |
| T041 | 12b_unterminated_quote.csv (India) | 5 rows (rows before the bad quote) | 0 filtered, 0 skipped. rows_read 5, rows_matched 5 | Yes (recorded since Stage 3): `Bad data found in csv file`, row 7, start position 483 |
| T047 | 17_headers_variant.csv (India) | 4 rows | 0 filtered, **8 skipped** after Stage 2 (0 filtered, 0 skipped before it). rows_read 8 | No |

What the 48 passes cover [S]: the original sample (T001 to T004), a clean baseline for seven countries plus a lower-case country name (T005 to T013), location edge cases (T014 to T019), quoting and escaping (T020, T021), Unicode (T022 to T027), UTF-8 BOM (T028, T029), LF line endings (T031, T032), reordered and extra columns (T033, T034), semicolon and tab delimiters with the delimiter supplied (T036, T037, T039), empty values (T042), header-only file (T043), all rows malformed (T045, T046) and volume files (T048 to T052).

## 3. Nodify findings (library behaviour)

1. **Selection re-entrancy [R].** Nodify mutates the collection bound to `SelectedItems` during a click or drag. Also binding `SelectedItem` and setting it from a `CollectionChanged` handler throws `InvalidOperationException: Cannot change ObservableCollection during a CollectionChanged event` (stack trace seen when dragging a node). Working pattern: bind only `SelectedItems`; derive `SelectedNode` (read-only) from the collection; iterate a copy when deleting. Severity: high until fixed. After the fix, node selection worked (reported by the user).
2. **XAML namespace.** `https://miroiu.github.io/nodify` resolves. A build that did not resolve it produced no `NodifyEditor` at all [R].
3. **Binding contract.** `NodeInput`/`NodeOutput` expose `Anchor` (bound `OneWayToSource`) and `IsConnected`. `PendingConnection` needs `Source` and `Target`. `PendingConnection.PreviewTarget` is an object, not a Point. The pending connection and disconnect commands take their data in the shapes the example project shows [R]. These were learned from the Nodify.StateMachine example, not from XML documentation [R].
4. **Viewport API.** `ViewportLocation`, `ViewportZoom`, `ActualWidth` and `ActualHeight` are enough to convert a screen point to graph coordinates. Our drop and palette code uses them [R]; whether the palette click and drag now work is not confirmed in this revision [U].
5. **Documentation.** XML documentation is good for individual types and properties. It does not describe the node template structure (input/output connector templates, footer) or the pending-connection contract; the example project was the only source for these [R].

## 4. ETLBox findings (library behaviour)

All members below were found by reflection [A] on `ETLBox.dll`, `ETLBox.Csv.dll`, `ETLBox.Json.dll` and `ETLBox.Licensing.dll` (3.11.1).

### 4.1 Delimiters, headers and encoding

- `CsvSource<T>` has **no** `Delimiter` property [A]. It has `Configuration`, a `CsvHelper.Configuration.CsvConfiguration`, which exposes `Delimiter`, `DetectDelimiter`, `DetectDelimiterValues`, `Encoding`, `HasHeaderRecord`, `HeaderValidated` and `PrepareHeaderForMatch` [A]. `CsvSource<T>` also exposes `FieldHeaders`, `ReadFieldHeaders(string uri)`, `BadDataException` and `CsvContext` [A].
- Semicolon and tab files are read correctly when the delimiter is supplied (T036, T037, T039) [S]. The code sets `csvSource.Configuration.Delimiter` directly when the delimiter is not a comma, with no reflection, and maps the two-character text `\t` to a tab [C]. Earlier notes that said non-comma delimiters are "not supported" were wrong.
- A semicolon file read with the wrong delimiter (T038) ends in an error, but it comes from our own header check, not from ETLBox [S] (section 5).
- `Encoding` exists on `CsvSource<T>` and on `Configuration` [A]. T030 shows a Windows-1252 file read with the default encoding returns 0 rows and no error [S]. Whether setting the encoding fixes it is untested [U].
- `PrepareHeaderForMatch` and `HeaderValidated` exist [A]. T047 shows header variants (different case, padded names) lose all rows without an error [S]. Whether these two settings fix it is untested [U].

### 4.2 Errors and bad rows

- Error routing exists in the API: `LinkErrorTo` on `CsvSource<T>`, `RowTransformation`, `MemoryDestination<T>`, `JsonSource` and `JsonDestination`; `Network.LinkAllErrorTo`; `ErrorCount` and `ErrorSource` [A]. Earlier notes that said "no error routing" were wrong at API level.
- Its behaviour has **not** been tested [U]. T040 (extra-field row accepted) and T041 (unterminated quote: an error, but no output) are the two cases to try it on.
- Run from the Stage 2 UI, the unterminated-quote input (T041) fails with `Bad data found in csv file`, naming the row, the start position and the raw record. The run stops and delivers none of the valid rows before the bad one [R]. In `SpikeResults.csv` the same input shows `rows_read` 5 and `rows_matched` 5, and the raw record in the error text contains rows 6 and 7 joined, so the unclosed quote swallowed the next line too [S]. Fail-fast versus partial output is a design choice, not yet made.
- Returning `null` from a `RowTransformation` drops the row (used for filtering); the idiom worked in all 48 passing tests [S].

### 4.3 Execution and progress

- `Network.ExecuteAsync()` (with `CancellationToken` overloads) and `Network.Cancel()` exist [A]. Earlier notes that said "no async execution" were wrong. We still run the synchronous call inside `Task.Run` [U: confirm in `PipelineRunner`].
- `ProgressCount` and `OnProgress` exist on the components [A]. Behaviour untested [U].
- `Network.AllowMultipleExecutions` (`bool?`) exists [A]. Our rule is to build new components and a new network on every Run; whether that setting makes reuse safe is untested [U].

### 4.4 JSON

- `JsonDestination` constructors are `()` and `(string fileName)`; `JsonSource` constructors are `()`, `(string uri)` and `(string uri, ResourceType)`. Neither declares public properties, so there is no option for indentation or format [A].
- Whether `JsonSource` can read from something other than a file depends on the members of `ResourceType`, which the API dump did not list [U].
- The pipeline uses the generic `JsonDestination<ExpandoObject>`, `JsonSource<ExpandoObject>` and `VoidDestination` [C]. The API dump covered only the non-generic JSON types, so the members of the generic ones are not inspected [U]; the two statements above may not hold for them.
- Indented output is produced in our code with `System.Text.Json` (BCL) [R].

### 4.5 Memory destination

- `MemoryDestination<T>` exposes `Data` and `ClearDataOnStart` [A]. It was used for all 52 tests [S]. Behaviour with more than a few thousand rows beyond T048 to T052 (up to 5,001 rows in memory, 152 to 196 ms in the latest run; 117 to 176 ms in the one before) is untested [U].

## 5. Defects in our own code (not library findings)

1. **T047: header variants lose every row.** Ideal 4 rows; actual 0 filtered [S]. The file has headers like ` First_Name ` and `Location` [M]. Before Stage 2 the result was 0 filtered and 0 skipped, so the 8 rows read were not accounted for anywhere. After Stage 2 the same 8 rows appear as `skipped = 8`, so the counts now reconcile, but they are reported as malformed rows when the real cause is a header mismatch. Still no error. Mechanism [C]: the header pre-check compares column names case-insensitively after trimming, so it passes, but the per-row lookup of the location column is exact and case-sensitive, so every row fails it and is counted as skipped. An earlier note that reported "8 filtered, 8 parsed" contradicted `SpikeResults.csv` and was wrong.
2. **T041: the run fails and none of the 5 valid rows is delivered.** Ideal: 5 rows plus an error [M]. Until Stage 3 the spike suite showed 0 / 0 / 0 with no error text, because the test runner did not record the error for `observe` tests; it does now, and `detail` holds `Bad data found in csv file`, row 7, start position 483 and the raw record [S]. `rows_read` 5 and `rows_matched` 5 mean ETLBox delivered the five valid rows to our filter before raising the error, so they were not lost in reading [S]. Our own `catch` block then returns `[]` for both outputs together with the error and the counts so far, so the valid rows are discarded by our error handling [C]. The error comes from the CSV reader and our runner reports it. Whether ETLBox's error routing (`LinkErrorTo`, `BadDataException`) could keep the valid rows is untested [U].
3. **T030: Windows-1252 rows are read but never match, with no error** [S]. `rows_read` 8, `rows_matched` 0, `rows_not_matching` 8, so the rows arrive garbled and are counted as ordinary non-matches; the 2 rows that should match are among them [M]. Whether ETLBox's decoding or our handling is responsible is not established [U].
4. **T040: the too-long row is accepted.** The returned ids are `1,2,4,5`; only rows 1, 2 and 5 are well-formed India rows [S][M]. `rows_read` 7 = 4 matched (ids 1, 2, 4, 5) + 1 not matching (id 6, USA) + 2 skipped [S][M]. Which two rows are skipped is not recorded [U]. An earlier note that called all four returned rows "well-formed" was wrong.
5. **T038: right error, wrong cause.** A wrong delimiter yields a missing-column message [S]. The message names a column when the real problem is the delimiter. The named column changed between runs: `location` before Stage 2, `first_name` after it, because the required-column list is now the parse fields first and the location column last, so `first_name` is the first one found missing [C].
6. **The header pre-check is hand-written.** It splits the first line on the delimiter, which does not handle quoted header names, and duplicates what `ReadFieldHeaders`, `HasHeaderRecord` and `HeaderValidated` provide [A]. The four column names are no longer hardcoded: the required columns now come from the node settings (`ParseFields` plus the location column) [C]. Its case-insensitive trimmed match is inconsistent with the exact row lookup (item 1). Known and deferred.
7. **Reconciliation exists now but is weaker than it looks.** Since Stage 3, `SpikeResults.csv` has `rows_read`, `rows_matched`, `rows_not_matching`, `rows_skipped` and `reconciles`. All 49 results that have counts reconcile (the three error tests T035, T038 and T044 have none), and in every strict test `rows_matched` and `rows_skipped` equal the filtered and skipped counts [S]. But `reconciles = true` only says no row disappeared between ETLBox's reader and our counters. It is `true` for T030, T040, T041 and T047, all of which differ from the ideal result. It is not compared against the rows in the file, so a row the reader never delivers cannot show up as a mismatch.
8. **Window startup bug.** The view model field was null when `OnInitialized` ran inside `InitializeComponent`, so palette and Spike buttons did nothing and the editor looked blank. Fixed by creating the view model in the constructor and setting `DataContext` there [R].
9. **Theme switch is inverted.** The Dark and Light chrome backgrounds are swapped [R]. Not fixed.
10. **Tooling.** In an early step the agent built a Canvas editor instead of Nodify. It had to be banned in the instructions and repaired [R]. On 2026-10-23, a Copilot request to rewrite this file stalled and left `EVALUATION.md` deleted in the working tree (`git status`); the file was recovered from git and the earlier copy. Several earlier agent-written conclusions contradicted the raw results (delimiter support, the license limit, "52 passed", T040, T047). Evaluation text written by an agent needs checking against `SpikeResults.csv`. Later failures were all in Visual Studio's Copilot chat: a dropped streaming connection (`ResponseStreamReadFailure`, socket error 10054) and an `Invalid prompt: ... flagged as potentially violating our usage policy` error (HTTP 200) on 2026-10-23 and again on 2026-10-24, the second time right after the agent had read this file to append a section. None involved our code. The agent also lost track of its own plan step numbers, although it had already changed the code for Stage 3. This file was updated outside Copilot [R].
11. **Failure attribution is not reliable [C].** Any exception from the first network (CSV reader, filter or JSON writer) is attributed to CsvSource, so a JSON write failure would be blamed on the wrong node. An exception while building the parsed JSON string is attributed to ToJson although it belongs to ParseJson. This breaks the Stage 3 rule to attribute a failure only when it is certain. Not fixed.
12. **Smaller points in `PipelineCore` [C].** With an empty country every row is accepted and counted as matched, with no malformed check (the UI requires a country, a direct caller does not). `TryIndentJson` returns the raw text when it cannot parse the JSON, and `TryDelete` ignores failures, both silently. `PipelineRunner.Run(PipelineSettings)` always returns "CSV file path is required." because it passes an empty path; check whether anything calls it.
13. **The Generate DLL button first saved into the project folder.** Its default folder was the CSV's folder, `SampleData\TestData`, so the DLL and seven generated `.cs` files landed inside the project, where the default compile glob would add them to the app, and they appeared in `git status`. Found on first use (screenshot); the files were deleted by hand. Fixed afterwards: the dialog starts in the Documents folder, and a folder that is, or sits under, a folder with a `.csproj` or `.sln` is refused with a message in the Issues tab [R: the agent's description; the user's manual run reported OK].

## 6. Design limits (our scope, not the libraries)

- Stage 2 runs only the linear five-node chain: CsvSource, FilterByCountry, ToJson, ParseJson, ResultJson. Branches or extra nodes produce an Issue and nothing runs [R].
- One country per run. All values are strings; no number or date parsing.
- Output is two JSON strings shown in the UI. The generate-DLL step exists (section 10) and, as specified, needs the .NET SDK's reference pack on the machine that generates it.

## 7. Licensing

- The free edition's published limit is 5,000 rows per DataFlow (vendor documentation, checked earlier in the project) [R]. Without a license, a limit breach raises `No valid license key was found` [R].
- First run without a license: T048 to T052 failed with that error; all smaller tests passed [R]. The raw file of that run was not kept; only the earlier report records it. Re-checked on 2026-10-24 through the generated DLL in a separate console project with no license (`18_volume_source_over_limit.csv`, India): the run failed with `No valid license key was found. The free version of ETLBox is limited to processing 5000 rows per connector...` and the result showed `read 5001, matched 1203` [R: console output]. With the license copied next to `ETLBox.dll` the same run gave `no error`, `read 12000, matched 3000`, and 3,000 records in the parsed JSON (the output file was checked: 3,000 records) [R].
- Evidence on what is counted: with the trial license present all five pass [S]. In the unlicensed run these inputs were the following (`rows_read` is measured by the runner and matches the file sizes in the manifest):

| Test | Source rows read (`rows_read`) [S] | Rows output [S] |
|---|---|---|
| T048 | 12,000 | 3,000 |
| T049 | 12,000 | 4,000 |
| T050 | 12,000 | 5,000 |
| T051 | 8,000 | 5,001 |
| T052 | 8,000 | 2,999 |

  T048, T049 and T052 output fewer than 5,000 rows but still failed, so a limit on **output** rows is ruled out. The failures are consistent with a limit on rows **read**. `LicenseService.Check(int progressCount, string featureName)` points the same way [A]. The unlicensed run above failed on the 5,001st row read, because our read counter had reached 5,001 when the error was raised, so rows 1 to 5,000 were processed. ETLBox's own message says the limit is per connector; which connector reaches it first is not established [U]. Not yet tested: source files of exactly 5,000 and 5,001 rows.
- Registration options: `LicenseService.CurrentKey` [A], and, per the vendor, the `etlbox` environment variable or an `etlbox.lic` file next to `ETLBox.dll` [R]. The trial license file was used. Our `.csproj` copies it to the output when it exists. The file is git-ignored, personal and must never be committed or quoted. It expires 2026-11-01.
- A generated DLL that uses ETLBox needs the same license at runtime (Stage 4 consideration).

## 8. Stage 2 status (graph run)

A `PipelineRunner` walks the graph from CsvSource, builds a new ETLBox network on every Run and shows per-node counts [R]. The user ran the seven acceptance checks:

| # | Check | Result |
|---|---|---|
| 1 | Build with 0 errors; Sample graph, `employee_list.csv`, a country in the file: both panes filled, counts on nodes | pass (reported by the user; no screenshot or numbers kept) |
| 2 | Click Run twice without changes: identical output | pass (reported by the user; no screenshot or numbers kept) |
| 3 | Delete one connection, Run: an Issue names the missing link, nothing executes | pass (reported by the user; no screenshot or numbers kept) |
| 4 | Country with no match: `[]` in both panes, no error | pass (reported by the user; no screenshot or numbers kept) |
| 5 | `12_ragged_rows.csv`, India: counts reconcile (read = matched + not matching + skipped) | pass (reported by the user; no screenshot or numbers kept) |
| 6 | `12b_unterminated_quote.csv`, India: record whether counts reconcile and whether an Issue appears | observed (screenshot): all five nodes show Failed, rows 0, with `Pipeline error: Bad data found in csv file`, row 7, start position 483 and the raw record. The run stops with a clear error; none of the 5 valid rows is delivered |
| 7 | Spike runner after Stage 2: same 48 / 0 / 4 as section 2 | pass [S]: 48 / 0 / 4, and no result field differs between the runs after Stage 2 and after Stage 3 |

## 9. Stage 3 status (country dropdown, node states, test runner)

The agent implemented all three parts but its session ended on a service error before it recorded an acceptance run. What is verified and what is only reported:

**Verified from `SpikeResults.csv` [S] (test runner):**
- Five new columns are present: `rows_read`, `rows_matched`, `rows_not_matching`, `rows_skipped`, `reconciles`. 49 results have counts; T035, T038 and T044 (error tests) leave them blank, as asked.
- T041's `detail` now holds the error text.
- 48 PASS / 0 FAIL / 4 OBSERVED, with no result field changed from the run after Stage 2.

**Reported by the user as passing; screenshots, lists and numbers were not kept [R]:**

| # | Check | Result |
|---|---|---|
| 1 | Country dropdown for `00_original_employee_list.csv`, `01_baseline_clean.csv`, `02_location_edge_cases.csv` and `04_unicode.csv` | pass (reported). Expected [M], computed from the files with the spec rules outside the pipeline: Germany, India, USA; Brazil, Canada, Germany, India, Japan, United Kingdom, USA; British India, Germany, India, Indiana, Indonesia, United States, USA (India once); the Unicode file lists México and Mexico separately, with Türkiye, Österreich, 日本 and Côte d'Ivoire |
| 2 | `10_semicolon.csv` with `;` (Germany, India, USA) and with `,` (an Issue and an empty list) | pass (reported) |
| 3 | `12b_unterminated_quote.csv`: an Issue and an empty list, UI responsive; Run: only CsvSource shows Failed, the others show Not run | pass (reported) |
| 4 | Missing file path: an Issue, no crash | pass (reported) |
| 5 | `18_volume_source_over_limit.csv` (12,000 rows): Germany, India, USA, window stays responsive | pass (reported) |
| 6 | India on the original file, Run: 11 rows, as T001 | pass (reported) |
| 7 | Spike runner: 48 / 0 / 4, new columns present, T041 error recorded | pass [S] |

**Not reviewed here:** the code of the dropdown reader, the node-state logic and the runner changes. Whether the dropdown read uses ETLBox only, and whether it reuses the runner's delimiter setup, is not confirmed [U].

## 10. Stage 4 status (DLL generation)

**4a (refactor).** The pipeline logic moved into `Engine/PipelineCore.cs`. Every result column except `elapsed_ms` is identical in the runs before and after, and the sample graph still returns 11 rows for India [S][R].

**4b (generator).** Microsoft.CodeAnalysis.CSharp 4.14.0 (per the agent) [R]. `DllGenerator` copies five Engine files with the namespace line changed, adds global usings and a wrapper `GeneratedPipeline.Run(csvPath, country, delimiter = null)`, compiles with Roslyn and writes the DLL and the sources [C]. The generated DLL was run through the 52-test matrix twice (before and after the reference-assembly fix below): 48 PASS / 0 FAIL / 4 OBSERVED, and every column except `elapsed_ms` is identical to the normal runner and to Stage 3 [S]. A hostile `LocationColumn` value (`x"; throw new System.Exception("boom"); //`) compiled and stayed data: the run returned `Missing required column: x"; ...` [R]. Only that one setting was exercised. The generated wrapper rejects an empty country with "Country is required." and its header notes the license requirement; no license content is in it [C].

**Findings**
1. **Compile references (our mistake).** The first generator compiled against the runtime's implementation assemblies (the trusted platform list). The DLL ran in our own harness, but a separate project could not compile against it: `CS0012: The type 'Object' is defined in an assembly that is not referenced ... System.Private.CoreLib` [R: console output]. The harness loads the DLL at run time, so only compiling another project against it could expose this. The fix compiles against the reference assemblies of the SDK's `Microsoft.NETCore.App.Ref` pack plus the non-framework dependencies. The DLL then references CsvHelper, ETLBox, ETLBox.Csv, ETLBox.Json, System.Collections, System.Linq, System.Linq.Expressions, System.Runtime and System.Text.Json, with no `System.Private.*` [R: agent's list]. A separate console project with only the ETLBox 3.11.1 packages then compiled and ran the DLL.
2. **Design limit.** Generating a DLL needs the .NET SDK reference pack on the machine. Not tested on a machine without the SDK [U].
3. **License.** The DLL needs the ETLBox packages and a license at runtime; see the external runs below and section 7.
4. **Locked output.** Generating into a folder whose DLL was in use (held by the demo project's build) failed with access denied. It is now reported as an error [R].
5. **A run not reproduced.** The agent's DLL-mode run gave 43 PASS / 5 FAIL / 4 OBSERVED (T048 to T052 with the license error). The same command run by the user from the app's output folder gave 48 / 0 / 4. The cause is not established; probably a working folder without the license [U].
6. **Roslyn 4.14.0 supports C# 13 as its stable language level, with C# 14 in preview (per the agent) [R].** The embedded Engine files must stay within C# 13 syntax.

**External runs through the generated DLL, in a separate console project (2026-10-24) [R]**

| Run | Result |
|---|---|
| `00_original_employee_list.csv`, India, no license | `no error`, read 18, matched 11, 11 records (same as T001). The repeated names are in the source file |
| `18_volume_source_over_limit.csv`, India, no license | Failed with `No valid license key was found`; `read 5001, matched 1203`; parsed JSON `[]` |
| Same file, license next to `ETLBox.dll` | `no error`, read 12000, matched 3000, 3,000 records |

**4c (UI button).** A Generate DLL toolbar button, enabled only after a successful run and disabled before the first run, after a failed run, during a run and during generation. It asks for a file name, turns it into an assembly name, refuses reserved names such as `ETLBox`, generates on a background thread and shows the DLL path, the location of the `.cs` files and the license note. The user ran the checks and reported that all work: button states, generating twice into the same folder, the refused name, no freezing and the refusal of a folder inside a project [R]. Only the success dialog was seen as a screenshot; no other numbers or screenshots were kept. The first use revealed defect 5.13.

## 11. Open items

1. Try `LinkErrorTo` and `BadDataException` on T040 and T041; try `PrepareHeaderForMatch` for T047 and `Encoding` for T030 (4.1, 4.2).
2. Decide between fail-fast and partial output for bad data (T041, 4.2). A design choice.
3. Make failure attribution certain or remove it (5.11). The generated wrapper now rejects an empty country with "Country is required." (attributed to CsvSource) [C]; `PipelineCore` itself still accepts it (5.12).
4. Confirm the free-limit boundary with source files of exactly 5,000 and 5,001 rows and no license (one run already failed at the 5,001st row read, section 7).
5. Check `ResourceType` members and `AllowMultipleExecutions` (4.3, 4.4).
6. Fix the inverted theme (5.9).
7. The error text includes the raw record (full row data). Decide whether that is acceptable for real data.
8. Compare the runner's counts with ETLBox's own `ProgressCount` or an independent count, so a row the reader never delivers becomes visible (5.7) [U].
9. Check whether reading the country list counts toward the free-edition limit when no license is present, since it reads the file an extra time [U].
10. Try DLL generation on a machine without the .NET SDK; the reference pack is required (section 10) [U].

## 12. Recommendation (provisional)

- **Nodify** is usable for this editor: nodes, connections and drag worked in the running application, and the cost was learning the binding contract from an example project plus one selection re-entrancy trap (section 3) [R].
- **ETLBox** ran the full pipeline correctly on 48 of 52 inputs, including quoting, Unicode, BOM, LF, reordered columns and non-comma delimiters; the 8,000 to 12,000-row files took 152 to 196 ms in the latest run (117 to 176 ms in the one before) [S]. Without a license, inputs of about 8,000 rows and above failed [R].
- T030 and T040 lose or accept rows without any error. T047 loses all rows, counted as skipped with a misleading cause. T041 raises a clear error from the CSV reader and our runner now records it. ETLBox's own error routing has not been tried (section 4.2). No judgement on error handling should be made until section 11 item 1 is done.
- **DLL generation** works as specified. The generated DLL matches the app in all 52 tests [S], and it compiled and ran from a separate console project that has only the ETLBox packages, with a license needed for inputs above 5,000 rows [R]. The compile-references mistake (section 10) shows that such a DLL has to be tested by compiling a separate project against it, not only by loading it in our own harness.
