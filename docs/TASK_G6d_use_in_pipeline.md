# TASK G6d: "Use in pipeline" from Intake Studio, CSV part settings, lineage (branch g6)

Read first: CLAUDE.md, `reports/taskG6c-report.md` (section "For G6d"), `docs/INTAKE_PLAN.md` (lineage rules).
Standing rules: do not read, print, embed or copy `etlbox.lic` or any licence key. No new package. Do not change the detector rules,
`PipelineCore`, the legacy nodes or `SampleData/TestData/intake/` (read-only; work on temp copies).
Free memory 4 GB or more before each full run, `dotnet build-server shutdown` first, one test process at a time.
No retry loops, sleeps or skips to hide a failing test: report the full message. Tests never write `lineage.log` or `templates`
next to the test exe (use the existing redirects).

Decisions (already taken, do not ask):
- "Use in pipeline" creates or updates ONLY the CSV part node and connects it to the File intake node. It does not add ParseValue or
  DotNetObject (the user configures those as today).
- One CSV part per pipeline: if the File intake already feeds a CSV part, "Use in pipeline" asks (Yes/No, default No):
  "Replace the part chosen in 'csv-1' with this one?" Yes replaces the selector of that node (G6c rule: old read and columns are
  forgotten); No does nothing. If another File intake already feeds a CSV part, say so and do nothing.
- The Studio never changes the original file.

## Step 0: node look in the error state (found in G6c)

The G6c report says the error node was wider and overlapped ParseValue and cut DotNetObject, and the fix was to move the test's node
positions. That hides a real problem. In the app a long locator sentence must not widen the node: wrap it inside a fixed maximum
width (use the same token/width the other nodes use) and let the full sentence be in the Issues tab and a tooltip. Screenshot
(`INTAKE_SCREENS=1`, 1100x720, Light and Dark) with the SAME node positions as the ready picture, showing no overlap.
Also: the CSV part node shows the generic "Idle | rows: 0" and "Configured" lines. Show them only if they carry information, otherwise
hide them for this node type (like you judge best; say what you did).

## Step 1: Studio "Use in pipeline"

- In Intake Studio, on a table part (CSV or TSV) that `CsvPartSelector.FromSession` accepts, a button "Use in pipeline". Disabled with a
  tooltip containing the `message` of `FromSession` when it refuses (key-value, text, fixed width ...).
- It works from the Studio of a File intake node (the Studio knows its node). Layout edits and the saved layout of the Studio are
  used exactly as they are on screen (the session's current state), so the user can fix the layout first, then use it.
- After it runs the Studio shows one line: "Part 2 is now the CSV part of this pipeline." (or the refusal) and the canvas node
  appears next to the File intake node, connected, with the background read started (G6c step 3).
- Re-using the same part is a no-op with a line "This part is already the CSV part of the pipeline." (compare selector JSON).
- Everything token-styled, Light and Dark, 820x560 and 1100x720 with no clipping (screenshots).

## Step 2: CSV part settings (Configure)

- Double-click or Configure on the CSV part node opens a small read-only dialog: example file name, template name, part number and lines,
  expected delimiter / header / skip lines, column count and names, the example file's SHA-256 (first 12 characters), status.
  A button "Open in Intake Studio" opens the Studio of the connected File intake node (so the user can change the part and press
  "Use in pipeline" again). No editing of the selector in this dialog.
- Column selection for the CSV part (Data Studio): `DataStudioViewModel` already takes an injected read function. Probe first whether it
  can show the part through `CsvPartColumns` / the part overload with a small change. If yes, wire it so `node.ColumnSelection` works for a
  CSV part like it does for a CsvFile node and Execute honours it. If it needs more than a small change, do NOT build it: write what is
  missing in the report and leave ParseValue as the way to choose columns.

## Step 3: lineage

- Execute of a CSV part pipeline writes one `pipeline.executed` event with `AppLineage` (same rules as the other events: no paths, no
  user or machine names, no content): file name, SHA-256, template id and name, match and similarity, part number, first and last line,
  rows read / parsed / rejected / flagged / out, elapsed ms, cancelled yes/no. "Use in pipeline" writes `pipeline.part-chosen`
  (file name, SHA-256, template id and name, part number). The generated DLL still writes nothing.
- Tests: the events appear with exactly those fields (redirected log), none for a failed locate except `pipeline.executed` with the
  failure kind and no counts, and no path or content ever.

## Step 4: tests, screens, proof

- Tests (WpfHost, STA, empty binding trace): button enabled/disabled with the exact tooltip for key-value and text parts; create, replace
  (Yes and No), same part, second File intake; the node after "Use in pipeline" executes and equals the standalone CSV of the same part
  on i10 and on a longer temp copy of i10; the settings dialog fields; Open in Intake Studio; lineage; the node in the error state without overlap.
- Screenshots in `reports/g6d/` (Studio with the button and the confirmation line, canvas, settings dialog, error node), Light and Dark.
  Look at them yourself and say what you see.
- Three clean full runs in a row, legacy `--run-tests` counted from SpikeResults.csv (baseline 43 PASS / 5 FAIL T048-T052 / 4 OBSERVED).
- CLAUDE.md "Current state". `reports/taskG6d-report.md`: changes per file, test numbers with free memory and duration, what you could not
  verify, and "Manual checks for you" (numbered, what to click, what to expect).
- Commit per step; do not commit files you did not change.
