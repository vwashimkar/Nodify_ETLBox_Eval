# CSV workflow specification (draft 0.2)

Project: `Nodify_ETLBox_Eval`. Focus: one CSV path done well. Bigger ETL plans (REST and database sources, branching graphs, C# expressions) are paused; see `Out of scope` at the end.

Changes from 0.1: delimiter levels and value columns are configurable, preview rows are a setting (default 5), the first hop builds only the .NET object destination, styles are hand-written, a Carry columns setting was added, and the test data now exists in `composite/`.

Evidence tags: **[P]** proven in the first POC, **[A]** present in the ETLBox 3.11.1 API dump (`docs/etlbox-api-surface.md`), **[D]** read in ETLBox's public docs, **[U]** unverified, check before relying on it.

---

## 1. Decisions so far

| Topic | Decision |
|---|---|
| Delimiters | `:` between the sample id and the component list, `\|` between components. The number of delimiter levels is configurable: the user adds as many as needed, each with its own character and mode. |
| Components and columns | Six components in the example, three value columns each, paired by position. |
| Where the values are | Chosen by the user, never hard-coded. The user marks the value columns in the grid (click headers, Ctrl-click, or a range), sets the group size, and groups are formed from the marked columns in file order. The marked columns are stored by name. |
| Output | One row per component (long form) by default, wide form as an option. **Waiting for your confirmation** using `composite/CONFIRM_THIS.md`. |
| Preview rows | A number box, default 5. |
| Destinations in the first hop | .NET object only. |
| Styling | Hand-written styles from design tokens. Revisit after the first slice. |

Worked example. The input cell is

```
S100:compid1|compid2|compid3| compid4|compid5|compid6
```

and 18 value columns follow it in the file (three per component, in component order). The long-form output is six rows:

```
sample_id  component  value_a  value_b  value_c
S100       compid1    <col 1>  <col 2>  <col 3>
S100       compid2    <col 4>  <col 5>  <col 6>
...        ...        ...      ...      ...
S100       compid6    <col 16> <col 17> <col 18>
```

Note the space before `compid4`. Trimming is a setting (default on).

Open point: **Carry columns.** Columns such as `id`, `batch` and `name` can be copied onto every output row. It is off by default; confirm whether you want it on.
---

## 2. Scrutiny: what the request does not yet cover

| # | Gap | Why it matters | Decision |
|---|---|---|---|
| 1 | Preview and run may use different parsers | In the first POC the hand-written header check and ETLBox's row lookup disagreed (test T047). A preview that parses differently from the run shows the user something untrue. | One parser. Preview reads through ETLBox's `CsvSource` and stops after the number of rows the user sets, default 5 (`Cancel` is in the API dump [A]); it never uses a separate hand-written reader. |
| 2 | Sample is not the file | A few rows can hide rows with 1 or 40 components, empty cells, stray quotes. | After a test on the sample, offer **Scan whole file**: counts per row of components, empty cells, rows that fail, shown as a small distribution table. |
| 3 | Ignored columns stored by position | If the supplier adds a column, positions shift and the wrong columns are ignored. | Store by header name. Mode: *Ignore these* or *Keep only these*. A policy for new unknown columns: ignore (default) or stop the run. Without a header row, columns are named `Col1`, `Col2`, and the dialog says so. |
| 4 | File details are missing | Header row yes/no, encoding, quote character, BOM, blank lines. | Settings with defaults. Only the delimiter is proven [P]; `Encoding` and `PrepareHeaderForMatch` are in the API dump [A] but untested [U], so test each before exposing it. |
| 5 | Serialize then deserialize adds a hop | It costs disk and a second read, and loses nothing only because all values are text. | Keep it, as you asked, but call the node **JSON checkpoint**: it has a path, shows file size and row count, and is marked *stale* when anything upstream changes. |
| 6 | Parse logic as a single cell setting | Real cells have empty items, trailing `\|`, duplicates, different counts. | Explicit policies (section 5): trim, drop empty items, duplicate ids, count mismatch. Each has a default and a visible counter in the test result. |
| 7 | No "what happened to my row" view | A row that fails is invisible in a long output. | Test result and run summary list rejected rows with the reason, the record number and the file line where the record starts (they differ when a quoted cell contains a line break). Clicking a result row highlights its source cell. |
| 8 | Test and Execute not separated | Test must never write to a database or call a REST service. | **Test** = sample only, never writes to external destinations, shows the result in a dialog. **Execute** = whole file, writes. Execute asks for confirmation when the destination overwrites. |
| 9 | Destinations only named | "Configurable" needs a defined shape. | Destination adapters (section 6) with schema-driven settings, each with *Test destination*. The first hop builds only the .NET object destination; the others follow through the same mechanism. |
| 10 | No schema step before the destination | Names and types of output columns are decided somewhere. | An **Output schema** table in the ParseValue dialog: column name, type (text default), nullable. The .NET object (and later Excel and database columns) come from it. |
| 11 | Run behaviour | Progress, cancel, errors, summary. | `ExecuteAsync` with a `CancellationToken` [A]; progress via `OnProgress`/`ProgressCount` [A, untested]. A summary dialog at the end. |
| 12 | Project persistence | Users lose work without Save/Open. | Save and open the graph as a versioned JSON project file. Secrets are never written to it. |
| 13 | Undo and redo | Expected in any visual tool. | Required for node moves, connections and settings changes. |
| 14 | "Seamless" is not testable | Needs concrete rules. | Section 7: states, enabled/disabled rules, keyboard, empty and error messages. |
| 15 | Generate DLL must keep working | The first POC's DLL generator embeds five engine files; new nodes need new files in it. | A rule in section 8: every new engine file is added to the generator's embedded list, and the DLL parity test runs again. |
| 16 | Free-edition limit | 5,000 rows read per connector without a licence [P]. | You will have a licence. Show the licence state in the status bar so a failed large run is not a mystery. |

---

## 3. The improved workflow

Nodes on the canvas, left to right. Each node has a **Configure…** button that opens a dialog with the same layout (section 7).

```
CsvFile  ->  JsonCheckpoint  ->  ParseValue  ->  Destination
```

1. **CsvFile node.** Settings: file path (Browse…), separator (presets: comma, semicolon, tab, pipe, custom), header row, encoding. Validation runs as you type: the file exists, can be opened, and its first line splits into at least one column. Status goes *Not configured* to *Configured*.
2. **Load preview** (button on the node, enabled only when Configured). Opens the **Data Studio** dialog, modal, with the first N rows. N is a number box beside the button (default 5).
3. **In the dialog, Columns tab.** One checkbox per column. Bulk actions: all, none, invert, filter by name. Mode: ignore these / keep only these. A toggle hides ignored columns in the grid.
4. **Test dataset.** Switches to the *Result* tab and shows the dataset as it leaves the node: kept columns, the first N rows, row and column counts.
5. **Save.** The selection is stored by column name on the node.
6. **JsonCheckpoint node.** *Serialize* writes the whole file (kept columns only) as indented JSON. *Deserialize* reads it back for the next node. The dialog shows path, rows, file size and a *stale* marker.
7. **ParseValue node.** Dialog layout:
   - top: pick the source column;
   - centre: the sample grid. Click a cell and its full value appears in the bottom pane, with delimiter characters highlighted;
   - right: **Delimiters** (an ordered list of levels, with Add level and Remove), **Components** (the list the selected row produces), **Value columns** (marked in the grid; shown as a list of names), **Group size** and mismatch policy, **Carry columns**, **Output shape** (long or wide) and **Output schema**;
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
| Encoding | UTF-8 | [U] test with a Windows-1252 file (T030 in the first POC). |
| Quote character | `"` | [U] |
| New-column policy | ignore | When a column appears that was not in the saved selection. |
| Preview rows | 5 | Number box, 1 to 1,000. |

### JsonCheckpoint

| Setting | Default |
|---|---|
| Path | `<project>/work/<node>.json` |
| Indented | yes |
| Overwrite | yes |

### ParseValue

| Setting | Default | Notes |
|---|---|---|
| Source column | none | Chosen by clicking a cell or from a list. |
| Delimiter levels | two rows | Add or remove levels. Level 1 `:` splits once (key, rest). Level 2 `\|` splits all (components). Level 3 and beyond split each component into sub-parts, which become extra output columns. |
| Trim items | yes | |
| Drop empty items | yes | Handles a trailing `\|`. |
| Duplicate component ids in one row | keep and flag | Or reject the row. |
| Value columns | marked by the user | Marked in the grid and stored by name, never by position. Groups follow file order. |
| Group size | 3 | Value columns per component. |
| Number of groups | automatic | Marked columns divided by group size. |
| Carry columns | none | Columns copied onto every output row (for example `id`). |
| Count mismatch (components vs groups) | reject the row, with reason | Or pair what fits and flag the row. |
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
marked value columns, file order, chunked by group size G -> group 1..m
component Ck pairs with group k      -> one output row per component (long)
```

Rules the parser must follow, each with a test:

- Components and groups are paired by position.
- If `n` is not the number of available groups, apply the mismatch policy.
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

---

## 9. Test data and acceptance

Built and delivered as `composite/` (made-up data; copy to `SampleData/TestData/composite/`). Its `README.md` explains the manifest. All 12 tests were checked with a separate script; the expected files agree with the inputs.

| Test | File | What it checks | Rows in / out / rejected / flagged |
|---|---|---|---|
| C01 | `c01_basic.csv` | Long-form output, 18 value columns, extra columns not carried | 4 / 24 / 0 / 0 |
| C02 | `c02_whitespace.csv` | Trim, trailing `\|`, empty item between pipes, padded key | 4 / 24 / 0 / 0 |
| C03 | `c03_count_mismatch.csv` | Policy reject: 4 and 8 components against 6 groups | 4 / 12 / 2 / 0 |
| C03b | same file | Policy pair-what-fits | 4 / 22 / 0 / 2 |
| C04 | `c04_bad_cells.csv` | No key delimiter, empty value, empty key; duplicate ids flagged and kept | 5 / 12 / 3 / 1 |
| C05 | `c05_quoting.csv` | Quoted comma in a name and in a value; a record spanning two lines; the reject of record 4 is on file line 6 | 4 / 18 / 1 / 0 |
| C06 | `c06_semicolon.csv` | Separator `;`, decimal commas in values | 3 / 18 / 0 / 0 |
| C07 | `c07_no_header.csv` | No header row: `Col1`…; source `Col4`; values `Col5..Col22` | 3 / 18 / 0 / 0 |
| C08 | `c08_many_extra_columns.csv` | 60 columns, 19 used | 3 / 18 / 0 / 0 |
| C08b | `c08b_columns_added.csv` | Supplier inserts two columns; value columns stored by name, same output as C08 | 3 / 18 / 0 / 0 |
| C09 | `c09_extra_levels.csv` | A third delimiter level; missing sub-part gives empty revision | 3 / 18 / 0 / 0 |
| C10 | `c10_large.csv` | 20,000 records, 20 rejected; checked by count and SHA-1; needs the licence | 20,000 / 119,880 / 20 / 0 |

Not covered by files, to be tested by hand: preview row count setting, the scan-whole-file step, encoding (Windows-1252) and the quote character.

Done means: the test run reports no unexplained difference from `expected_results.csv`, in the app and through the generated DLL, as in the first POC.

---

## 10. Build stages (each ends with something you can run and check)

| Stage | Content | Done when |
|---|---|---|
| 0 | Merge pending work, fix the inverted theme, add the design tokens | Both themes look right on the existing window |
| 1 | Settings schema, `IDialogService`, CsvFile node with validation and Load preview button | A wrong path or separator shows a clear message; a valid one enables the button |
| 2 | Data Studio: grid with a rows-to-show setting, cell pane, ignore/keep columns, Test dataset | You can ignore 3 columns and see the result |
| 3 | JsonCheckpoint node | Serialize then deserialize gives back the same rows |
| 4 | ParseValue: engine with unit tests first (C01 to C09), then the dialog | The worked example in section 1 gives the 6 expected rows and C01 to C09 pass |
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

## Out of scope for now

REST and database as sources, destinations other than the .NET object (CSV, Excel, database, REST), branching graphs and joins, C# expressions in rules, parameters on the generated DLL, a profile store for connections.
