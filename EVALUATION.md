# Evaluation: Nodify 7.3.0 and ETLBox 3.11.1

Proof of concept: a visual ETL designer in WPF (net10.0-windows). A CSV is filtered by country, turned into JSON, parsed back to `first_name`, `last_name`, `email`, and shown in two panes. The graph is built with Nodify and executed with ETLBox. Only these two libraries (plus ETLBox.Csv, ETLBox.Json and the .NET BCL) are being evaluated.

Last revised: 2026-10-23. Status: Stage 1 (spike and test suite) complete. Stage 2 (graph run) implemented but **not yet accepted** (see section 7).

## How to read this document

Every claim carries an evidence tag. A claim without a tag does not belong here.

| Tag | Meaning |
|---|---|
| **[S]** | Result in `SpikeResults.csv` (52 tests, trial license present) |
| **[M]** | Test manifest or README in `SampleData/TestData/` (inputs, ideal results, the spec) |
| **[A]** | `docs/etlbox-api-surface.md`: public API found by reflection on the shipped assemblies. It lists member names found by a keyword scan; behaviour is NOT tested by it |
| **[R]** | Reported in earlier session notes or screenshots, not re-checked for this revision |
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
- `observe` tests have no pass condition. The four below record actual behaviour against the ideal result.

| Test | File | Ideal [M] | Actual [S] | Error shown? |
|---|---|---|---|---|
| T030 | 06_encoding_windows1252.csv (México) | 2 rows | 0 filtered, 0 skipped | No |
| T040 | 12_ragged_rows.csv (India) | 3 rows (ids 1, 2, 5) | 4 filtered, 2 skipped. SHA-1 of the returned ids equals SHA-1 of `1,2,4,5`, so the 10-field row 4 was accepted | No |
| T041 | 12b_unterminated_quote.csv (India) | 5 rows (rows before the bad quote) | 0 filtered, 0 skipped | No |
| T047 | 17_headers_variant.csv (India) | 4 rows | 0 filtered, 0 skipped | No |

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
- Semicolon and tab files are read correctly when the delimiter is supplied (T036, T037, T039) [S]. **How the spike applies the delimiter is not recorded here [U]; read `ETLBoxSpike.cs` and write it down.** Earlier notes that said non-comma delimiters are "not supported" were wrong.
- A semicolon file read with the wrong delimiter (T038) ends in an error, but it comes from our own header check, not from ETLBox [S] (section 5).
- `Encoding` exists on `CsvSource<T>` and on `Configuration` [A]. T030 shows a Windows-1252 file read with the default encoding returns 0 rows and no error [S]. Whether setting the encoding fixes it is untested [U].
- `PrepareHeaderForMatch` and `HeaderValidated` exist [A]. T047 shows header variants (different case, padded names) lose all rows without an error [S]. Whether these two settings fix it is untested [U].

### 4.2 Errors and bad rows

- Error routing exists in the API: `LinkErrorTo` on `CsvSource<T>`, `RowTransformation`, `MemoryDestination<T>`, `JsonSource` and `JsonDestination`; `Network.LinkAllErrorTo`; `ErrorCount` and `ErrorSource` [A]. Earlier notes that said "no error routing" were wrong at API level.
- Its behaviour has **not** been tested [U]. T040 (extra-field row accepted) and T041 (valid rows lost before an unterminated quote, no error) are the two cases to try it on.
- Returning `null` from a `RowTransformation` drops the row (used for filtering); the idiom worked in all 48 passing tests [S].

### 4.3 Execution and progress

- `Network.ExecuteAsync()` (with `CancellationToken` overloads) and `Network.Cancel()` exist [A]. Earlier notes that said "no async execution" were wrong. We still run the synchronous call inside `Task.Run` [U: confirm in `PipelineRunner`].
- `ProgressCount` and `OnProgress` exist on the components [A]. Behaviour untested [U].
- `Network.AllowMultipleExecutions` (`bool?`) exists [A]. Our rule is to build new components and a new network on every Run; whether that setting makes reuse safe is untested [U].

### 4.4 JSON

- `JsonDestination` constructors are `()` and `(string fileName)`; `JsonSource` constructors are `()`, `(string uri)` and `(string uri, ResourceType)`. Neither declares public properties, so there is no option for indentation or format [A].
- Whether `JsonSource` can read from something other than a file depends on the members of `ResourceType`, which the API dump did not list [U].
- Indented output is produced in our code with `System.Text.Json` (BCL) [R].

### 4.5 Memory destination

- `MemoryDestination<T>` exposes `Data` and `ClearDataOnStart` [A]. It was used for all 52 tests [S]. Behaviour with more than a few thousand rows beyond T048 to T052 (up to 5,001 rows in memory, 117 to 176 ms) is untested [U].

## 5. Defects in our own code (not library findings)

1. **T047: header variants lose every row silently.** 0 filtered, 0 skipped, no error; ideal 4 [S]. The file has headers like ` First_Name ` and `LOCATION` [M]. Nothing in the result accounts for the 8 rows read. An earlier note that reported "8 filtered, 8 parsed" contradicted `SpikeResults.csv` and was wrong.
2. **T041: 5 valid rows lost without an error** [S]. Attribution (ETLBox read failure versus how our code handles the exception) is not established [U].
3. **T030: Windows-1252 rows lost without an error** [S]. Same attribution question.
4. **T040: the too-long row is accepted.** The returned ids are `1,2,4,5`; only rows 1, 2 and 5 are well-formed India rows [S][M]. The reason for `skipped = 2` is not established [U]. An earlier note that called all four returned rows "well-formed" was wrong.
5. **T038: right error, wrong cause.** A wrong delimiter yields `Missing required column: location` [S]. The message names a column when the real problem is the delimiter.
6. **The header pre-check is hand-written and hardcodes four column names** (`location`, `first_name`, `last_name`, `email`) in the Stage 1 spike. It duplicates what `ReadFieldHeaders`, `HasHeaderRecord` and `HeaderValidated` provide [A], and it ignores the `LocationColumn` and `Fields` node settings. Known and deferred; confirm whether Stage 2 changed it [U].
7. **Rows are not reconciled.** There is no check that rows read = matched + not matching + skipped. Items 1 to 3 would have been caught by one. This is the first thing to add (section 8).
8. **Window startup bug.** The view model field was null when `OnInitialized` ran inside `InitializeComponent`, so palette and Spike buttons did nothing and the editor looked blank. Fixed by creating the view model in the constructor and setting `DataContext` there [R].
9. **Theme switch is inverted.** The Dark and Light chrome backgrounds are swapped [R]. Not fixed.
10. **Tooling.** In an early step the agent built a Canvas editor instead of Nodify. It had to be banned in the instructions and repaired [R]. On 2026-10-23, a Copilot request to rewrite this file stalled and left `EVALUATION.md` deleted in the working tree (`git status`); the file was recovered from git and the earlier copy. Several earlier agent-written conclusions contradicted the raw results (delimiter support, the license limit, "52 passed", T040, T047). Evaluation text written by an agent needs checking against `SpikeResults.csv`.

## 6. Design limits (our scope, not the libraries)

- Stage 2 runs only the linear five-node chain: CsvSource, FilterByCountry, ToJson, ParseJson, ResultJson. Branches or extra nodes produce an Issue and nothing runs [R].
- One country per run. All values are strings; no number or date parsing.
- Output is two JSON strings shown in the UI; the "generate a DLL" step is Stage 4 and not started.

## 7. Licensing

- The free edition's published limit is 5,000 rows per DataFlow (vendor documentation, checked earlier in the project) [R]. Without a license, a limit breach raises `No valid license key was found` [R].
- First run without a license: T048 to T052 failed with that error; all smaller tests passed [R]. The raw file of that run was not kept; only the earlier report records it. **To be re-checked by removing `etlbox.lic` from the output folder and re-running.**
- Evidence on what is counted: with the trial license present all five pass [S]. In the unlicensed run these inputs were:

| Test | Source rows read [M] | Rows output [S] |
|---|---|---|
| T048 | 12,000 | 3,000 |
| T049 | 12,000 | 4,000 |
| T050 | 12,000 | 5,000 |
| T051 | 8,000 | 5,001 |
| T052 | 8,000 | 2,999 |

  T048, T049 and T052 output fewer than 5,000 rows but still failed, so a limit on **output** rows is ruled out. The failures are consistent with a limit on rows **read**. `LicenseService.Check(int progressCount, string featureName)` points the same way [A]. The exact threshold is **untested**: the largest passing unlicensed input was a file of 61 lines (`07_line_endings_lf.csv`) and the smallest failing one has 8,000 rows.
- Registration options: `LicenseService.CurrentKey` [A], and, per the vendor, the `etlbox` environment variable or an `etlbox.lic` file next to `ETLBox.dll` [R]. The trial license file was used. Our `.csproj` copies it to the output when it exists. The file is git-ignored, personal and must never be committed or quoted. It expires 2026-11-01.
- A generated DLL that uses ETLBox needs the same license at runtime (Stage 4 consideration).

## 8. Stage 2 status (graph run)

Reported by the agent, not yet confirmed by a person: a `PipelineRunner` walks the graph from CsvSource, builds a new ETLBox network on every Run, shows per-node counts and reports a regression run of 48 PASS / 0 FAIL / 4 OBSERVED [R]. Acceptance checks:

| # | Check | Result |
|---|---|---|
| 1 | Build with 0 errors; Sample graph, `employee_list.csv`, a country in the file: both panes filled, counts on nodes | not recorded |
| 2 | Click Run twice without changes: identical output | not recorded |
| 3 | Delete one connection, Run: an Issue names the missing link, nothing executes | not recorded |
| 4 | Country with no match: `[]` in both panes, no error | not recorded |
| 5 | `12_ragged_rows.csv`, India: counts reconcile (read = matched + not matching + skipped) | not recorded |
| 6 | `12b_unterminated_quote.csv`, India: record whether counts reconcile and whether an Issue appears | not recorded |
| 7 | Spike runner after Stage 2: same 48 / 0 / 4 as section 2 | not recorded |

## 9. Open items

1. Add reconciliation columns (`rows_read`, `rows_matched`, `rows_not_matching`, `rows_skipped`) to the test output, so a silent loss shows up as a failed check.
2. Try `LinkErrorTo` and `BadDataException` on T040 and T041; try `PrepareHeaderForMatch` for T047 and `Encoding` for T030 (4.1, 4.2).
3. Record how the delimiter is applied in `ETLBoxSpike.cs` (4.1).
4. Find the free-limit threshold with files of 4,999, 5,000 and 5,001 source rows and no license (section 7).
5. Check `ResourceType` members and `AllowMultipleExecutions` (4.3, 4.4).
6. Fix the inverted theme (5.9).

## 10. Recommendation (provisional)

- **Nodify** is usable for this editor: nodes, connections and drag worked in the running application, and the cost was learning the binding contract from an example project plus one selection re-entrancy trap (section 3) [R].
- **ETLBox** ran the full pipeline correctly on 48 of 52 inputs, including quoting, Unicode, BOM, LF, reordered columns and non-comma delimiters, in 117 to 176 ms for the 8,000 to 12,000-row files [S]. Without a license, inputs of about 8,000 rows and above failed [R].
- The four observed cases lose or accept rows without any error. Whether that is ETLBox behaviour or our handling is not yet known, and ETLBox's own error routing has not been tried (section 4.2). No judgement on error handling should be made until the section 9 items 1 and 2 are done.
