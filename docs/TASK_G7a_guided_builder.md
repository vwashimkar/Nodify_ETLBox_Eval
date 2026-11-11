# TASK G7a: Guided builder (file -> table -> columns -> try -> DLL) for a non-technical user

Branch: continue on `g6` (or create `g7` from it; say which). Read CLAUDE.md first. All standing constraints stay:
no licence key read/printed/embedded; originals under SampleData/TestData/intake/ and real/expected files are read-only (work on temp copies);
YamlDotNet is the only approved extra package; lineage only in lineage.log (no paths, names, content); tests never write lineage.log or templates next to
the test exe; free memory 4 GB+ before full runs, `dotnet build-server shutdown` first, one test process at a time; no retry loops, sleeps or skips to hide
failing tests; report full failure messages. Do not change the detector rules, CsvFile definitions/hash, or the DLL generator output for existing pipelines.

## Why
The user could not get a pipeline built by hand on the canvas (wiring four nodes, Execute greyed until ParseValue is configured, a wrong part chosen
gives "Bad data found in csv"). The goal is: pick one instrument file, confirm the table, optionally split one column, try it, save a DLL.
The guided builder creates the SAME chain the canvas already uses:
File intake -> CSV part -> ParseValue -> DotNetObject (ParseValue only when the user turns it on; otherwise File intake -> CSV part -> DotNetObject
if that chain is supported, else a pass-through ParseValue you create hidden; probe first and report which).
The canvas stays as the "Advanced" view. Nothing existing is removed.

## Step 0: probe (report before building)
a. Can DotNetObject be connected directly to the CSV part Rows port? If not, what is the smallest honest way to offer "no splitting"? Report; do not guess.
b. What exactly keeps Execute disabled for the chain (list every condition and message)? The guided flow must make each condition true or explain it.
c. Which existing classes give: scan (Intake engine), part list, template apply, "Use in pipeline" creation, run/Execute, Generate DLL gate. Reuse them; no copies.

## Step 1: entry point
New toolbar button "Guided" next to Intake Studio (does not replace the canvas). Opens one plain WPF window "Build a reader" with Light/Dark tokens,
a step bar (1 File, 2 Table, 3 Columns, 4 Try it, 5 Save as DLL), Back / Next, and plain-English text. No node, port or JSON words anywhere in this window.

## Step 2: the five steps
1. File: Browse or drop one file. Scan starts automatically with progress and Cancel. If a saved layout (template) matches, say "Known layout: <name>" and
   preselect its table.
2. Table: show the best table found with a preview (first rows) and the question "Is this your data?". Under it a short list of the other tables
   (kind, lines, rows); choosing one updates the preview. Tables that fail a read (for example extra lines from the next table) show a plain sentence,
   "This table includes lines from another table. Pick <name> instead." when a neighbouring table reads cleanly. Next is disabled while the chosen table
   cannot be read. Merge/split/edit live behind an "Advanced: open Intake Studio" link that returns to this step afterwards with the user's layout.
3. Columns: checklist of the table's columns (names from the run's reader, never the selector's) with a preview. One optional control:
   "Split a column into parts": choose the column, ONE separator (comma, semicolon, colon, pipe, tab, space, other), and the names of the parts (default Part1, Part2...).
   Multi-level splitting is Advanced (the existing ParseValue settings) and is not offered here. Keep-all-other-columns default ON.
   Plain-English error if the separator never occurs in the first rows ("Nothing to split: '|' was not found in column X").
4. Try it: button "Try on the sample file" runs the real pipeline (same runner as Execute) and shows the rows (first 200) and the counts
   (rows read, rows kept, rows rejected with the reason in a sentence). Next is disabled until a try has succeeded after the last change.
   Optional "Test on another file": drop a second file of the same instrument; the app locates the table by structure (the DLL's own locator),
   runs the pipeline and shows "Same layout found. N rows." or the plain reason it did not match. This is the new-file-different-row-count check.
5. Save as DLL: name field (default from the template/file name), folder picker, button "Create DLL". Uses the existing generator and gate.
   After success: list of files written (DLL + the YamlDotNet.dll note), and the sentence that the user's ETLBox licence is required where the DLL runs.
   Also offers "Save this setup" (.etlproj) and "Open in the canvas (Advanced)".

## Step 3: wiring and state
- The wizard builds/updates the pipeline through the same project model the canvas uses, so "Open in the canvas" shows the chain and Execute/Generate DLL
  work there with identical results (assert equal PipelineHash and equal rows).
- One CSV part per pipeline stays. Starting the wizard on a project that already has a chain asks Yes/No (default No) before replacing.
- The layout is saved as a template automatically (name from the file, no duplicate names: replace on a same-name save after Yes/No, default No), lineage
  events as in G6 plus guided.started / guided.saved-dll (no paths/content).
- All long work off the UI thread, cancel everywhere, no sleeps.

## Step 4: plain-language errors
Create one small mapping layer (class + tests) from the engine's messages to sentences a lab user understands, with the technical text available under
a "Details" expander. Cover at least: bad quoting / bad data row, table not found in a new file, column count changed, header changed, several copies of the
table, file too large, YamlDotNet missing, licence missing/expired (ETLBox stops about 5,000 rows).

## Step 5: tests and proof
- View-model tests for every step transition (enabled/disabled Next, Back keeps choices), the neighbour-table suggestion on r03 (part 9-18 clean, 9-26 not),
  "no splitting" path, one-separator split, Try, second-file test (use two temp copies of one real/test file with different row counts, generated, never the originals),
  Create DLL gate, duplicate template name, the error mapping.
- Parity: a pipeline built by the wizard equals the same pipeline built on the canvas (hash and rows), for i10, r03 (Results part) and one TSV part.
- WpfHost STA test: window opens, Light/Dark, each step renders with no binding errors, text not clipped at 100% and 150% scaling; screenshots of all five steps.
- Generated DLL from a wizard pipeline run in the existing console consumer on a second file with a different row count: same columns, correct count.
- Three clean full runs in a row. Legacy stays 43 PASS / 5 FAIL / 4 OBSERVED.

## Report: reports/taskG7a-report.md
Step 0 answers first. Then what was built, test counts per run, screenshots list, every deviation, anything you changed outside the new files
(with the reason; ask before changing older tests), and "For G7b" (ideas: drag-and-drop of the DLL settings, several columns to split, a saved-readers library).
