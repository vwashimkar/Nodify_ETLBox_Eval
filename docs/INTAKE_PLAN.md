# Plan: Intake stage (mixed-format files from instruments), v0.3

Status: draft 0.3 (v0.2 plus the corrections listed in "What changed in v0.3"). The user has approved the direction. Nothing here is built. When you approve it, copy it to `docs/INTAKE_PLAN.md` and run the stage prompts one at a time, as before.

## 0a. What changed in v0.3

- **Count corrected:** `i01_sample.txt` has 17 marker segments (not 18) plus 2 text blocks, so 19 containers. The large file (the text repeated N times) has 18N + 1 containers in marker mode (17 segments and 1 text block per copy, plus one). The earlier "18" counted an indented trap line.
- **Real files** (six vendor exports, folder `real/`) replace the earlier plan to wait for the user's own files. They are Tier B cases with hand-written truth; `r05` and `r06` are a hold-out. They showed that the key-value rule, the table rule, blank-line handling and the encoding rule below needed to be wider than v0.2 said.
- **UTF-16 with a BOM is supported** (real SoftMax Pro exports are UTF-16). UTF-32 and UTF-16 without a BOM are refused with a message.

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

Alteryx One loaded the sample as ONE table: 218 rows, one string column `column1`, text split on commas into `column2`, `column3` ..., JSON, XML, CSV and prose mixed in one grid, and "Data Insights" such as "Number of column1 = 170". Nothing was recognised. That is the baseline to beat: for the sample, 17 typed containers plus 2 text blocks, in order, each with its own preview, found with and without markers.

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
4. **Table (csv or tsv)**: consecutive lines that have the same field count, counted quote-aware across lines (a quoted field may span lines), for the best of the candidate delimiters `,` `;` `|` tab (best = most consistent count, fewest ambiguous quotes; decimal commas must not beat a `;` that is consistent). At least 3 lines, or 2 lines with 3 or more fields. A **blank line ends a table**, so two tables with the same layout separated by a blank line are two containers. A field count that differs by one (a trailing delimiter in the header, trailing empty cells cut off, a row with extra fields) is tolerated with confidence Likely. A table whose header changes with no blank line before it is split at the change (Guessed). A line made only of delimiters (`,,,,`) or only `""` is not blank: it may sit inside a region but may not start or end one. Header guess: first row has no numeric field while the next rows do (shown as Guessed; the user decides). **Preamble lines** directly before a table (for example `# Report ...`) are offered as skip lines of that table. Footer rows such as `TOTAL` stay inside the table.
5. **Key-value**: 3 or more consecutive lines of one form: `Name: value`; `Name = value` (also with a leading `* `); quoted `"Key","value"` rows whose field count may vary; `Key<TAB>value`; or several `Key: value` pairs on one line. No nesting. This is the common instrument metadata header. It is not YAML; it becomes a one-row table of metadata that can later be carried onto the rows of a table (planned, section 11).
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

- Ground truth (`intake_test_data.zip`): i01 the stress file (17 marker segments plus 2 text blocks), i02 CRLF, i03 BOM, i04 to i07 marker problems and edge cases, i08 six single-format files without markers, **i09 the stress file with the markers removed** and **i10 an instrument-style file** (20 `Key: value` lines, a CSV table, 2 footer lines), each with an expected file made by a separate reference scanner, not by the code under test.
- **Tiers** for auto-detect: Tier A rows must be right (format and exact lines) and fail the build; Tier B rows are best effort and are REPORTED (right, merged, split, wrong format) without failing, each with the reason it is hard. Report the Tier B scorecard in every detector task.
- Large file generated at test time (the stress file repeated N times: 18N + 1 containers in marker mode): N = 2,000 (13 MB) and N = 20,000 (130 MB, marked Slow, run once and report time and memory). Memory must not grow with N.
- Property check: joining all containers and markers back in order reproduces the file's lines (nothing lost or duplicated).
- Cancel tests repeat 20 times on temp copies; everything slow uses `TestTimeout`; ETLBox rules in CLAUDE.md apply to the CSV reads in G6.
- **Real files**: six real vendor exports (`real/r01` to `r06`: Luminex, SoftMax Pro (UTF-16), EnVision, QuantStudio, Gen5, PHERAstar) with hand-written truth (`real/expected/`). All rows are Tier B. `r05` and `r06` are a hold-out: no rule is tuned on them and they are run once at the end of G2. The user's own exports (for example Metrohm tiamo) are added later as more cases.

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

# Prompt for stages G0 and G1 (paste into a fresh Claude Code session, after Task F)

Before you start (you, not Claude Code): create a branch from master (`git switch -c intake`), copy `INTAKE_PLAN.md` to `docs/`, add the CLAUDE.md lines above, unzip `intake_test_data.zip` into `Nodify_ETLBox/SampleData/TestData/intake/` (so `i01_sample.txt` and `expected/` are there), commit, and start `claude` in the repo.

```
Read CLAUDE.md, docs/INTAKE_PLAN.md (sections 0, 4 to 10 and 13 to 14), docs/CSV_WORKFLOW_SPEC.md (section 0), docs/lessons-learned.md and SampleData/TestData/intake/README.md. Then read Engine/Csv/CsvRecordReader.cs, CsvDialectGuesser.cs and the TestTimeout helper in the tests.

Tasks G0 and G1: probes, then the intake foundation. No UI, no auto-detection yet (that is G2), no DLL change, no new package. Do not touch: EVALUATION.md, README.md, docs/, etlbox.lic, Engine/Generation/, the legacy nodes, Run, Engine/PipelineCore.cs, the palette code. Engine code has no WPF types, no assembly attributes and no test-only code (it will be embedded in generated DLLs later). Keep to about 60 tool calls; commit after each numbered step that builds and passes; if you run out, stop at the end of a finished step and say exactly where.

0. G0 probes (a `--probe-intake` switch next to `--probe-csv`; results in reports/probe-intake.md, each fact marked [P] proven by this run or [U] unproven):
   a. Can ETLBox 3.11.1 CsvSource read from a stream or a TextReader instead of a file path? Report its public API first, then try a small in-memory CSV.
   b. Same question for ETLBox.Json's JsonSource.
   c. Speed and peak memory of a byte-level line scan (tracking offsets) over a 130 MB file made of i01_sample.txt repeated 20,000 times (generate in the temp folder, delete afterwards).
   d. Cost of quote-aware field counting (a line splitter that counts delimiter candidates , ; | and tab outside quotes, across lines) over the same file.
   Report findings only; do not build on them.

1. Model (Engine/Intake/, namespace EtlDesigner.Engine.Intake): immutable records `IntakeItem` (a factory for a read-only stream, source kind "file", name, size, received UTC, an id), `IntakeSettings` (mode: AutoDetect, Markers, MarkersThenAutoDetect; marker profile with the defaults of plan section 5; max file size in MB, default 500; encoding name, default utf-8), `ContainerInfo` (every column of the expected *_segments.csv files in the test data, plus a confidence field that markers set to Sure), `IntakeFileInfo` (name, size, SHA-256, encoding, BOM, line ending kind, line count), `IntakeResult` (run id, UTC start and end, tool version, file info, containers, file-level warnings, elapsed ms, cancelled, error). Serialize and Deserialize with enums as strings and round-trip tests. Add `IIntakeFactory`, `IConfigStore`, `IRawArchive` and `ILineageLog` as INTERFACES only, with XML comments saying what they are for (plan sections 9 and 10), and one trivial File implementation of the intake factory plus a registry; nothing else is implemented.

2. Foundation (Engine/Intake/): a streaming line indexer that works on an IntakeItem stream: blocks, LF, CRLF and CR line ends, byte offsets from the start of the file (a UTF-8 BOM is skipped but counted), never keeps content in memory, never trims, SHA-256 computed in the same pass (do not read the file twice), progress about every 250 ms and at the end (bytes read, file size, containers so far, phase), cancel checked on every line with the file released at once. A missing file, an unreadable file, an empty file and a file above the size limit give a clear message in the result, no exception. A reader `ReadLines(item, first byte, last byte, maxLines)` returns lines by offsets.

3. Marker mode (Engine/Intake/MarkerScanner.cs) on top of the indexer, implementing plan section 5 and the contract in the test data README exactly (read them twice; i01 has trap lines). The result is an IntakeResult.

4. Tests (xUnit; slow things under TestTimeout):
   a. Data-driven over SampleData/TestData/intake: for i01 to i07 compare EVERY column of expected/<name>_segments.csv, and for i06 the file-level warning. The expected files are ground truth: never edit them; if a case disagrees with the plan text, stop and report which one.
   b. Property test: rebuild the line list from the containers and prove no line is lost or duplicated.
   c. ReadLines returns exactly the content lines (the trailing tab and spaces in i07 preserved; CRLF and BOM files give the same text with different offsets).
   d. Large file: the stress text repeated N = 2,000 times in a temp folder gives 18N+1 containers in marker mode; peak memory does not grow with N (compare N = 200 and N = 2,000 and report the numbers). N = 20,000 as a separate test with Trait "Slow", skipped unless an environment variable is set; run it once and report time and memory.
   e. Cancel: after the first containers of the large file, 20 times in a loop, deleting the temp file afterwards (proves the file is released), all under TestTimeout. Progress is reported at least once and the last report equals the final totals. Missing file, empty file, file above the limit: clear messages.
   f. In AutoDetect mode the foundation returns no containers yet and a message "auto-detection comes in the next stage" (placeholder); the i08 and i09 and i10 files are not tested here.
   g. A registry test: the file factory is found by kind, an unknown kind gives a clear message.

Build, run dotnet test with --blame-hang-timeout 60s (exact summary line, three runs in a row) and the legacy --run-tests (baseline 43 pass, 5 fail, 4 observed). Update the Current state in CLAUDE.md (the only doc you may edit). Write reports/taskG1-report.md: probe findings, what changed per step, test names and results, the large-file numbers, every rule you chose that the plan does not state (mark [U]), and what you could not verify. Reply in at most 5 lines.
```

## What you do after it finishes

1. Send me `reports/taskG1-report.md` and `reports/probe-intake.md`.
2. Start collecting 3 to 5 real instrument exports (private values replaced, and the name of the software). I will fold them in as test cases before the detector (G2) is written.
