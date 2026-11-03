# CSV workflow specification (draft 0.4)

Project: `Nodify_ETLBox_Eval`. Focus: one CSV path done well. Bigger ETL plans (REST and database sources, branching graphs, C# expressions) are paused; see `Out of scope` at the end.

Changes from 0.3: ETLBox facts settled by the probe run of 2026-11-02 (new section 0); skip lines, encoding, quote character, no-header names, cancel and progress are now known; rejects report a record number only (no line number).

Changes from 0.2: the mapping of components to columns is now an editable set of slots, not a rule; fewer than six components is normal; carry columns are on by default when the file has a header; the JSON checkpoint keeps the shape the user configures; the file is loaded first and configured afterwards. The test data in `composite/` was rebuilt for these changes.

Evidence tags: **[P]** proven in the first POC, **[A]** present in the ETLBox 3.11.1 API dump (`docs/etlbox-api-surface.md`), **[D]** read in ETLBox's public docs, **[U]** unverified, check before relying on it.

---

## 0. ETLBox facts settled by probes (2026-11-02)

Source: `docs/csv-probe-results.md`, produced by `--probe-csv` (ETLBox 3.11.1, `CsvSource<ExpandoObject>`). The trial key was expired during the run; every probe stayed under 5,000 rows read, so none was affected.

| Topic | Fact | Setting to use | Tag |
|---|---|---|---|
| No header | With the header off, columns are named `Field1`, `Field2`, … (1-based). `c07_no_header.csv` gave `Field1`…`Field28`. We rename them to `Col1`, `Col2`, … ourselves. | `Configuration.HasHeaderRecord = false` | [P] |
| Encoding | With the default, the Windows-1252 file's "México" row is not found. With the Windows-1252 encoding it reads `México\|Puebla`. | `CsvSource.Encoding` | [P] |
| Quote character | A single quote works, and an embedded delimiter inside the quotes stays in one cell. | `Configuration.Quote` | [P] |
| Delimiter and quoting | `;` works. `"Rao, Asha"` and `"2,5"` stay intact. | `Configuration.Delimiter` | [P] |
| Skip lines | There is no `SkipLines` property, but a delegate does it. With `ShouldSkipRecord` set to skip while `args.Row.Parser.Row <= N`, `c14_preamble.csv` (3 lines before the header) gave the correct header and first data row, with the header on and with it off. Row numbers seen by the delegate are 1-based. Not tested: a preamble that contains a quote character, and quoted multi-line records before the header. | `Configuration.ShouldSkipRecord` | [P] plain case; [U] the two cases named |
| Stop after N rows | On `c10_large.csv` (20,000 records), cancelling after 5 rows read exactly 5 in 1 to 29 ms, with a cancellation token and with `Cancel()`. After 1,000 rows it read exactly 1,000 in 46 to 58 ms. The file could be opened exclusively right after each run, and 10 cancel-and-reopen cycles in a row all worked. A preview of N rows stays fast on a big file. | cancel from inside the row step through a token the reader owns | [P] |
| Cancel from outside | **Never pass the caller's token to ETLBox.** A caller's cancel that lands while ETLBox is still starting the network makes `ExecuteAsync` wait forever (reproduced 2026-11-03: 3 of 3 runs hung for more than 10 s). The probe of 2026-11-02 only cancelled after rows were flowing, so it did not show this. The reader checks the caller's token before starting and on every row and stops ETLBox through its own token. The caller's cancel is noticed only when the next row arrives. | own token, cancelled in the row step | [P] fix; [U] slow-cancel case |
| Cancel result | A cancel that we cause ends the run with `OperationCanceledException`. The preview and the Cancel button must treat it as a normal end, not as an error. | catch it | [P] |
| Progress | `OnProgress` is called for every row, not at intervals. It cost about 10 ms per 1,000 rows (48 ms against 38 ms). Its last value was 1,001 for 1,000 rows delivered, so do not use it as the delivered row count. The UI must throttle its own updates (for example every 100 ms). | `OnProgress` | [P] |
| Line numbers | ETLBox does not expose a line or record number per good row (`Parser.Row` and `RawRow` read 0). **Decision:** we count records ourselves with a counter in the pipeline (1-based, from the first data record; header and skipped lines not counted). A record that spans two lines counts once (`c05_quoting.csv`: 4 records, 6 lines, the counter gave 4). Rejects therefore report a **record number**, not a line number. | `RowTransformation` counter | [P] |
| Bad file data | A structural error (unclosed quote, test file 12b) is delivered through `LinkErrorTo` as an `ETLBoxError` with `ErrorText`, `ReportTime`, `ExceptionType` (`ETLBoxException`) and `Source`. `RecordAsJson` is not available and `Context` is empty. No exception was thrown in that run. The row number is only inside the text (`Row: 7 - Start Position: 483 -- RawRecord: ...`) and counts the header as row 1, so for a file with a header and no skipped lines the record number is Row minus 1. Parsing that text is fragile: keep the pattern in one place and test it. How the number shifts with skipped lines, and whether the good rows before the error are delivered, are not established. | `LinkErrorTo` | [P] content; [U] the two open points |
| Still not tested | A full read of a file above 5,000 rows (needs a valid key); `PrepareHeaderForMatch`; duplicate header names. | | [U] |

## 1. Decisions so far

| Topic | Decision |
|---|---|
| Who uses it | A lab technician working with instrument exports. The tool must let them load a file, look at it, and then configure it. Nothing about a file's layout is assumed. |
| Delimiters | `:` between the sample id and the component list, `\|` between components, as in the example. The number of levels is configurable: add as many as needed, each with its own character and mode. |
| Components and columns | **The pairing is an example, not a rule.** Each component position (a *slot*) has its own list of columns, chosen by the user in the grid. Slots can differ in size and can skip columns. An *Auto-fill* helper proposes groups of a chosen size, and every slot can then be edited. Columns are stored by name. |
| How many components | Up to a maximum the instrument produces (6 for these), never required. A record with 1 to 6 components is normal. A record with more components than slots is the exception: reject by default, or pair what fits and flag it. |
| Repeated component names in one cell | Allowed. Flagging or rejecting them are options. |
| Carry columns | When the file has column names, all columns that are not value columns or the source cell are copied onto every output row by default; the user can untick any. Without a header the generated names (`Col1`, …) can be carried by choice. |
| Output shape | One row per component (long) by default, wide as an option. |
| JSON | The transformed JSON persists in the shape the user configures: flat (one object per output row) or nested (one object per input record with a components list). The JSON checkpoint can sit after CsvFile (raw rows) or after ParseValue (transformed rows). |
| Files | May or may not have a header. The technician loads the file first and sets separator, header and lines to skip afterwards, with the preview reloading live. |
| Preview rows | A number box, default 5. |
| Destinations in the first hop | .NET object only. |
| Styling | Hand-written styles from design tokens. Revisit after the first slice. |

Worked example (an example only; the mapping is configurable). The input cell is

```
S100:compid1|compid2|compid3| compid4|compid5|compid6
```

and the user has mapped slot 1 to columns `r01 r02 r03`, slot 2 to `r04 r05 r06`, and so on to slot 6 (`r16 r17 r18`). The long-form output is six rows:

```
sample_id  component  value_a  value_b  value_c
S100       compid1    <r01>    <r02>    <r03>
S100       compid2    <r04>    <r05>    <r06>
...        ...        ...      ...      ...
S100       compid6    <r16>    <r17>    <r18>
```

Note the space before `compid4`. Trimming is a setting (default on). A record with only `compid1|compid2` produces two rows and ignores slots 3 to 6.
---

## 2. Scrutiny: what the request does not yet cover

| # | Gap | Why it matters | Decision |
|---|---|---|---|
| 1 | Preview and run may use different parsers | In the first POC the hand-written header check and ETLBox's row lookup disagreed (test T047). A preview that parses differently from the run shows the user something untrue. | One parser. Preview reads through ETLBox's `CsvSource` and stops after the number of rows the user sets, default 5 (`Cancel` is in the API dump [A]); it never uses a separate hand-written reader. |
| 2 | Sample is not the file | A few rows can hide rows with 1 or 40 components, empty cells, stray quotes. | After a test on the sample, offer **Scan whole file**: counts per row of components, empty cells, rows that fail, shown as a small distribution table. |
| 3 | Ignored columns stored by position | If the supplier adds a column, positions shift and the wrong columns are ignored. | Store by header name. Mode: *Ignore these* or *Keep only these*. A policy for new unknown columns: ignore (default) or stop the run. Without a header row, columns are named `Col1`, `Col2`, and the dialog says so. |
| 4 | File details are missing | Header row yes/no, encoding, quote character, BOM, blank lines. | Settings with defaults. Delimiter, encoding, quote character and no-header names are proven (section 0) [P]. `PrepareHeaderForMatch` is in the API dump [A] and untested [U]. BOM and blank lines were covered by the first POC tests T028 and T042 [P]. |
| 5 | Serialize then deserialize adds a hop | It costs disk and a second read, and loses nothing only because all values are text. | Keep it, as you asked, but call the node **JSON checkpoint**: it has a path, shows file size and row count, and is marked *stale* when anything upstream changes. It can sit after CsvFile (raw rows) or after ParseValue (transformed rows, in the shape the user configures). |
| 6 | Parse logic as a single cell setting | Real cells have empty items, trailing `\|`, duplicates, different counts. | Explicit policies (section 5): trim, drop empty items, repeated names, more components than slots. Each has a default and a visible counter in the test result. |
| 7 | No "what happened to my row" view | A row that fails is invisible in a long output. | Test result and run summary list rejected rows with the reason and the record number (counted by us, section 0). Clicking a result row highlights its source cell. |
| 8 | Test and Execute not separated | Test must never write to a database or call a REST service. | **Test** = sample only, never writes to external destinations, shows the result in a dialog. **Execute** = whole file, writes. Execute asks for confirmation when the destination overwrites. |
| 9 | Destinations only named | "Configurable" needs a defined shape. | Destination adapters (section 6) with schema-driven settings, each with *Test destination*. The first hop builds only the .NET object destination; the others follow through the same mechanism. |
| 10 | No schema step before the destination | Names and types of output columns are decided somewhere. | An **Output schema** table in the ParseValue dialog: column name, type (text default), nullable. The .NET object (and later Excel and database columns) come from it. |
| 11 | Run behaviour | Progress, cancel, errors, summary. | `ExecuteAsync` with a `CancellationToken` and `Cancel()` both work and end with `OperationCanceledException`; progress via `OnProgress` is per row, so throttle the UI (section 0) [P]. A summary dialog at the end. |
| 12 | Project persistence | Users lose work without Save/Open. | Save and open the graph as a versioned JSON project file. Secrets are never written to it. |
| 13 | Undo and redo | Expected in any visual tool. | Required for node moves, connections and settings changes. |
| 14 | "Seamless" is not testable | Needs concrete rules. | Section 7: states, enabled/disabled rules, keyboard, empty and error messages. |
| 15 | Generate DLL must keep working | The first POC's DLL generator embeds five engine files; new nodes need new files in it. | A rule in section 8: every new engine file is added to the generator's embedded list, and the DLL parity test runs again. |
| 16 | Free-edition limit | 5,000 rows read per connector without a licence [P]. | The trial licence expired on 2026-11-01 [P]; a new key is needed for C10 and for the large-file probes. Everything else in the test data is under 5,000 rows. Show the licence state in the status bar so a failed large run is not a mystery. |
| 17 | The mapping treated as a rule | Instruments differ, and a fixed stride breaks on the next file. | Slots with explicit columns. Auto-fill is only a helper. |
| 18 | Files with or without headers, possibly with lines before the data | The technician must be able to load first and decide afterwards. | Load preview as soon as a readable file is chosen, with guessed settings. Separator, header, lines to skip and encoding are controls in the dialog and reload the preview live. Skipping lines works through `ShouldSkipRecord` (section 0) [P]. |
| 19 | JSON shape | The transformed JSON must persist the way the user configured it. | Flat or nested shape, property names from the Output schema (section 4). |

---

## 3. The improved workflow

Nodes on the canvas, left to right. Each node has a **Configure…** button that opens a dialog with the same layout (section 7).

```
CsvFile  ->  JsonCheckpoint  ->  ParseValue  ->  Destination
```

1. **CsvFile node.** Choose the file (Browse…). As soon as the file exists and can be opened, the node is *Configured* with guessed settings: separator suggested from the first lines, header row guessed, UTF-8. The technician then looks at the file and adjusts: separator (presets plus custom), header row yes or no, lines to skip before the header, encoding.
2. **Load preview** (button on the node, enabled once a readable file is chosen). Opens the **Data Studio** dialog, modal, with the first N rows. N is a number box beside the button (default 5). The separator, header, skip-lines and encoding controls are also in the dialog and reload the preview live.
3. **In the dialog, Columns tab.** One checkbox per column. Bulk actions: all, none, invert, filter by name. Mode: ignore these / keep only these. A toggle hides ignored columns in the grid.
4. **Test dataset.** Switches to the *Result* tab and shows the dataset as it leaves the node: kept columns, the first N rows, row and column counts.
5. **Save.** The selection is stored by column name on the node.
6. **JsonCheckpoint node.** Can sit after CsvFile (raw rows, kept columns only) or after ParseValue (the transformed rows). The user configures the shape: flat (one object per row) or nested (one object per input record with a components list), with property names from the Output schema. *Serialize* writes the whole file; *Deserialize* reads it back for the next node. The dialog shows path, rows, file size and a *stale* marker.
7. **ParseValue node.** Dialog layout:
   - top: pick the source column;
   - centre: the sample grid. Click a cell and its full value appears in the bottom pane, with delimiter characters highlighted;
   - right: **Delimiters** (an ordered list of levels, with Add level and Remove), **Components** (the list the selected row produces), **Slots** (one row per component position, each holding the columns you marked in the grid; Auto-fill proposes groups and every slot can be edited), **Excess components** policy, **Carry columns**, **Output shape** (long or wide) and **Output schema**;
   - **Test parsing** runs on the sample and opens the Result tab with counts and rejects.
8. **Destination node.** In the first hop the only type is .NET object: set the type name, namespace and collection kind, then press **Test destination**, which shows the first rows it would produce.
9. **Execute.** Toolbar button. Runs the whole file with a progress bar and a Cancel button. At the end, a summary: rows read, parsed, rejected, written, elapsed time, and a link to the rejects file.
10. **Generate DLL.** Kept from the first POC; enabled after a successful Execute.

---

## 4. Node and dialog settings

### CsvFile

| Setting | Default | Notes |
|---|---|---|
| File path | none | Relative paths are resolved against the project file's folder. |
| Separator | `,` | Presets plus custom. `\t` typed as text means tab [P]. |
| Header row | yes | Without it, columns are `Col1`, `Col2`, … |
| Encoding | UTF-8 | `CsvSource.Encoding`; Windows-1252 proven (section 0) [P]. |
| Quote character | `"` | `Configuration.Quote`; a single quote proven (section 0) [P]. |
| New-column policy | ignore | When a column appears that was not in the saved selection. |
| Preview rows | 5 | Number box, 1 to 1,000. |
| Skip lines before header | 0 | Some files have lines before the header. Implemented with `ShouldSkipRecord` (section 0) [P]. A quote character inside the skipped lines is untested [U]. |

### JsonCheckpoint

| Setting | Default |
|---|---|
| Path | `<project>/work/<node>.json` |
| Indented | yes |
| Overwrite | yes |
| Shape | flat | `flat` (one object per output row) or `nested` (one object per input record with a components list). |

### ParseValue

| Setting | Default | Notes |
|---|---|---|
| Source column | none | Chosen by clicking a cell or from a list. |
| Delimiter levels | two rows | Add or remove levels. Level 1 `:` splits once (key, rest). Level 2 `\|` splits all (components). Level 3 and beyond split each component into sub-parts, which become extra output columns. |
| Trim items | yes | |
| Drop empty items | yes | Handles a trailing `\|`. |
| Repeated component names in one row | allow | Or flag the row, or reject it. |
| Slots | six, empty | One slot per component position. Each holds the columns for that position, chosen in the grid and stored by name. Slots may differ in size and skip columns. A record needs only as many slots as it has components. The number of slots is the instrument's maximum (6 here) and can be changed. |
| Auto-fill | off | Helper: from a start column, groups of a chosen size fill the slots in order. The result can then be edited slot by slot. |
| Carry columns | all other columns when the file has a header | Copied onto every output row under their own names. Without a header, the generated names (`Col1`, …) can be carried by choice. The user can untick any. |
| More components than slots | reject the row, with reason | Or pair what fits and flag the row. Fewer components than slots is normal and needs no setting. |
| Output shape | long | Or wide. |
| Output schema | key `sample_id`, `component`, then `value_a`…`value_c` | Names and types editable; text by default. |

---

## 5. Parsing model

```
cell text
  -> level 1 (":" once)              -> key, rest
  -> level 2 ("|" all)               -> items
  -> trim, drop empty                -> components C1..Cn
  -> level 3 ("/" each, optional)    -> sub-parts per component (extra columns)
slots S1..Sm: each a list of named columns chosen by the user
component Ck uses slot Sk            -> one output row per component (long)
```

Rules the parser must follow, each with a test:

- Component k uses slot k. The mapping is whatever the user set. Nothing assumes a stride, contiguous columns or a group size of 3.
- Fewer components than slots is normal; unused slots are ignored.
- More components than slots applies the excess policy (reject, or pair what fits and flag).
- A cell with no level-1 delimiter is rejected with the reason "no key delimiter". An empty key is rejected with "empty key".
- A missing sub-part (a component without the level-3 delimiter) gives an empty value, not a reject.
- An empty cell is rejected with the reason "empty value".
- The parser is a plain function with no WPF or ETLBox types, so it is tested in isolation.

---

## 6. Destinations (configurable by design)

A destination is a definition: id, display name, settings schema, a *test* operation and a *write* operation. The destination node's form is generated from the schema, the same way for every type. Adding a destination later means adding a definition and no new XAML.

**First hop: only the .NET object destination is built.** The rest of the table is the design the others will follow.

| Destination | Settings | Notes |
|---|---|---|
| .NET object | type name, namespace, collection kind (`List<T>`, `ObservableCollection<T>`, `DataTable`) | Type generated from the Output schema. Result available to the host app, and to the generated DLL. |
| CSV file | path, separator, header, encoding, overwrite or append | `CsvDestination` is not named in the docs I read [D]; check the API dump [U]. |
| Excel file | path, sheet name, overwrite or append | Package `ETLBox.Excel` exists [D]; classes [U]. |
| Database | provider, host, port, database, user, secret, table, create if missing, insert mode | Provider packages and classes [U]. Secrets are protected per Windows user and never saved in the project file. |
| REST call | URL, method, headers, auth (none or bearer), payload (one row per call or a JSON array per batch), batch size, timeout, retries, success codes | `JsonDestination` posts rows to web endpoints and takes a `HttpClient` [D]. |

**Test destination** never changes anything outside the app: the .NET object and file types show the first rows they would write; the database opens a connection and checks the table; REST sends nothing and shows the payload it would send, with an optional *Send one sample* button that asks first.

---

## 7. User experience rules

### Data Studio dialog (modal, resizable, remembers its size)

```
+----------------------------------------------------------------------+
| CSV preview: employees.csv         rows to show [ 5 ]      [Reload]  |
+----------------------------------------------------------------------+
| Separator [ , ]   Header [x]   Encoding [UTF-8]      5 rows · 12 cols|
+---------------+------------------------------------------------------+
| COLUMNS       |  #  | id | first_name | location    | sample | ...   |
| [filter...]   |  1  | 1  | Asha       | India|Pune  | S100:c1|c2|.. |
| [x] id        |  2  | 2  | Tom        | USA|Austin  | S101:c1|c2|.. |
| [ ] first_..  |  ...                                                 |
| [x] sample    |                                                      |
| Mode: ignore  |                                                      |
+---------------+------------------------------------------------------+
| SELECTED CELL   row 1 · column "sample"                      [Copy]  |
| S100:compid1|compid2|compid3| compid4|compid5|compid6                 |
+----------------------------------------------------------------------+
| [Test dataset]                                     [Cancel]  [Save]  |
+----------------------------------------------------------------------+
```

Tabs across the top of the dialog for the nodes that need them: *Data*, *Result*. The ParseValue dialog adds the right-hand parsing panel described in section 3.

### Rules

- **Enabled states.** *Load preview* is enabled only when the node is valid. *Test dataset* needs a loaded preview. *Execute* needs every node valid. *Generate DLL* needs a successful Execute.
- **Selection.** Clicking a cell selects it and fills the bottom pane. The pane shows the full text wrapped, with delimiter characters coloured and invisible characters (leading and trailing spaces, tabs) made visible.
- **Large values.** The grid truncates long text with an ellipsis; the bottom pane always shows all of it.
- **Empty state.** Before a file is chosen, the node says "Choose a CSV file to begin". No empty grids.
- **Errors say what to do.** For example: "Could not read the file. It may be open in another program. Close it and press Reload." Not an exception message.
- **Never block the window.** File reads and runs happen off the UI thread; a spinner and a Cancel button show for anything longer than half a second.
- **Keyboard.** Esc closes a dialog (asks first if unsaved changes), Ctrl+C copies the selected cell, F5 reloads the preview, Ctrl+S saves the project, Ctrl+Z and Ctrl+Y undo and redo.
- **Node card.** Header with icon and name, a status chip (Not configured, Configured, Ready, Running, Done, Failed), and row counts after a run.

### Look and feel

- Define design tokens in one resource dictionary: colours, spacing on an 8-pixel grid, corner radius, font sizes. No colour or number is written directly into a view.
- Fix the inverted Light/Dark theme first (known defect from the first POC); both themes must be checked for every dialog.
- Fonts: Segoe UI Variable for text, Cascadia Mono or Consolas for data. Icons from Segoe Fluent Icons with a fallback; no emoji.
- DataGrid: compact rows, a fixed header, row numbers, a clear selected-cell highlight, virtualised rows, column widths remembered.
- Decided: hand-written styles for now, to keep the evaluation to Nodify and ETLBox. Revisit after the first slice.

---

## 8. Architecture rules (also add to `.github/copilot-instructions.md`)

1. `Engine/` has no reference to WPF. All ETLBox calls live in `Engine/`.
2. The CSV parsing for preview and for the run is the same code path.
3. The composite-cell parser is a pure function with unit tests, in `Engine/Parsing/`.
4. No logic in code-behind. Dialogs open through an `IDialogService` and have their own view model.
5. Node settings are described by a schema; forms are generated from it. Destinations use the same mechanism.
6. Every new engine file is added to the DLL generator's embedded list, and the DLL test run is repeated.
7. A CLI switch (`--run-tests`, as before) runs the test matrix without the UI and writes a CSV of results.
8. No secret is written to the project file, a log, or an error message. `etlbox.lic` stays out of git.
9. Nodify: the `SelectedItems` re-entrancy trap and the pending-connection pattern from the first POC are written down in `docs/lessons-learned.md` and must be read before touching the editor.
10. The existing Roslyn use stays inside `Engine/Generation/`.
11. The slot mapping is data, not code. The parser must not assume a group size, contiguous columns or six components.

---

## 9. Test data and acceptance

Built and delivered as `composite/` (made-up data; copy to `SampleData/TestData/composite/`). Its `README.md` explains the manifest columns. All 20 tests were checked with a separate script, so the expected files agree with the inputs.

| Test | File | What it checks | In / out / rejected / flagged |
|---|---|---|---|
| C01 | `c01_basic.csv` | Long-form output with an auto-filled mapping, nothing carried | 4 / 24 / 0 / 0 |
| C02 | `c02_whitespace.csv` | Trim, trailing `\|`, empty item between pipes, padded key | 4 / 24 / 0 / 0 |
| C03 | `c03_variable_counts.csv` | 6, 4, 8 and 1 components: fewer is normal, 8 exceeds the slots and is rejected | 4 / 11 / 1 / 0 |
| C03b | same file | Excess policy pair-what-fits | 4 / 17 / 0 / 1 |
| C04 | `c04_bad_cells.csv` | No key delimiter, empty value, empty key; repeated names allowed | 5 / 12 / 3 / 0 |
| C04b | same file | Duplicate policy flag | 5 / 12 / 3 / 1 |
| C05 | `c05_quoting.csv` | Quoted commas; a record spanning two lines; record 4 is rejected | 4 / 18 / 1 / 0 |
| C06 | `c06_semicolon.csv` | Separator `;`, decimal commas | 3 / 18 / 0 / 0 |
| C07 | `c07_no_header.csv` | No header: `Col1`…, source `Col4`, values `Col5..Col22` | 3 / 18 / 0 / 0 |
| C07b | same file | Carry columns by generated names | 3 / 18 / 0 / 0 |
| C08 | `c08_many_extra_columns.csv` | 60 columns, 19 used | 3 / 18 / 0 / 0 |
| C08b | `c08b_columns_added.csv` | Supplier inserts two columns; mapping stored by name; same output as C08 | 3 / 18 / 0 / 0 |
| C09 | `c09_extra_levels.csv` | A third delimiter level; missing sub-part gives an empty revision | 3 / 18 / 0 / 0 |
| C10 | `c10_large.csv` | 20,000 records: 20 rejected, 20 with 3 components; count and SHA-1; needs the licence | 20,000 / 119,820 / 20 / 0 |
| C11 | `c01_basic.csv` | Carry `id,batch,name` | 4 / 24 / 0 / 0 |
| C11b | same file | Default carry: every column that is not a value column or the source cell | 4 / 24 / 0 / 0 |
| C12 | `c12_irregular_slots.csv` | Slots of 2, 4, 1, 3, 3 and 4 columns, one skipping columns, 1 to 6 components per record | 6 / 21 / 0 / 0 |
| C14 | `c14_preamble.csv` | Three lines before the header skipped (`ShouldSkipRecord`, section 0) [P] | 4 / 24 / 0 / 0 |
| C15 | `c01_basic.csv` | Transformed JSON, flat, carrying `id,name` | 4 / 24 / 0 / 0 |
| C16 | same file | Transformed JSON, nested | 4 / 24 / 0 / 0 |

JSON tests compare parsed JSON, not text. Not covered by files, to be tested by hand: the preview row count setting, the header guess, the scan-whole-file step, encoding (Windows-1252) and the quote character.

Done means: the test run reports no unexplained difference from `expected_results.csv`, in the app and through the generated DLL, as in the first POC.

---

## 10. Build stages (each ends with something you can run and check)

| Stage | Content | Done when |
|---|---|---|
| 0 | Merge pending work, fix the inverted theme, add the design tokens | Both themes look right on the existing window |
| 1 | Settings schema, `IDialogService`, CsvFile node with validation and Load preview button | A wrong path or separator shows a clear message; a valid one enables the button |
| 2 | Data Studio: grid with a rows-to-show setting, cell pane, ignore/keep columns, Test dataset | You can ignore 3 columns and see the result |
| 3 | JsonCheckpoint node | Serialize then deserialize gives back the same rows |
| 4 | ParseValue: engine with unit tests first (C01 to C12, C15, C16), then the dialog | The worked example in section 1 gives the 6 expected rows and those tests pass |
| 5 | Destination: .NET object only (others deferred) | Test destination and Execute work, and the type can be used from a separate WPF app |
| 6 | Execute with progress, cancel and summary; Generate DLL for the new nodes | DLL results equal app results |
| 7 | Save/open project, undo/redo, keyboard, empty states | A saved project reopens identically |

---

## 11. Grounding GitHub Copilot needs

1. This file, committed to the repo as `docs/CSV_WORKFLOW_SPEC.md`, plus the updated `.github/copilot-instructions.md` (section 8).
2. `docs/lessons-learned.md` (Nodify traps, the T047 mismatch, the connection-drag fix, the Copilot problems you saw).
3. The test data and `expected_results.csv` from section 9, written before the code.
4. C# interfaces and models first (settings schema, `INodeDefinition`, `IDestination`, parser input/output types), written by a human or reviewed before Copilot builds on them.
5. Wireframes for each dialog (section 7 has the first one) as images or text in `docs/`.
6. One stage per prompt, each with its done criteria and a "do not touch" list, as in the first POC.
7. A review loop: you paste Copilot's diff and test output back, and the result is checked against the evidence before the next stage.

## 12. Noted requirement (not scheduled): mixed instrument files, text part plus CSV part

Noted 2026-11-02. Not part of Stages 2 to 7 until the user says so.

**What the user described.** Instrument export files (titration software is the example) are not plain CSV. A file can start with free text, for example a header block of about 20 lines with method name, date, instrument and sample details, and the CSV table starts after that (for instance after line 20). The ETL needs a first node that reads the whole file and lets the user mark which part is which. The text part goes to one output and the CSV part goes to a CSV file reader node as its input. The two parts then travel down their own paths.

**What this means for the design (questions, not decisions).**

| Topic | Question | Note |
|---|---|---|
| Marking the parts | How does the user mark them: by line range, by a start marker text, or by clicking in a preview of the raw text? | The click-in-preview way matches the Data Studio idea. A marker (a line that starts with a given text) survives files whose header length changes. A fixed line number does not. |
| Several tables | Can one file hold more than one table or more than one text block? | Unknown until we see a real file. |
| Text output | What does the text part become: key/value pairs (`Method: ...`), a single text, or lines? | A key/value result could be carried onto every row of the CSV part (for example the sample name). |
| CSV reader | Does the CSV part use the existing CsvFile settings (separator, header, encoding)? | Yes, it should feed the same reader, so there is still one parser. Skip lines (`ShouldSkipRecord`, section 0) already covers a fixed-length preamble, but not a marker-based start, a text block after the table, or a table that ends before the end of the file. |
| Reading a part of a file | Can ETLBox read from a stream or a string, or only from a file path? | [U] Not probed. If it only takes a path, the CSV part would be written to a temporary file first. This must be probed before design. |

**What is not known.** I could not find a public example export file for these instruments, so nothing above is based on a real file. Tiamo is Metrohm's titration software and Mettler Toledo's is LabX; they have different export formats. The real format has to come from a real file.

**What is needed to go further.** One or more real export files, with sample names or other private values replaced. With them, the first step would be a short probe (like the CSV probe) of the layout, then the questions in the table.

## Out of scope for now

REST and database as sources, destinations other than the .NET object (CSV, Excel, database, REST), branching graphs and joins, C# expressions in rules, parameters on the generated DLL, a profile store for connections.
