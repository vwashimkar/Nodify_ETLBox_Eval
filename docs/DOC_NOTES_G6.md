# Notes to append to docs/INTAKE_PLAN.md (G6a to G6d, 2026-11-11)

## Decisions
- ONE CSV part per pipeline for the POC (no joins across parts, no branching). User decision 2026-11-10.
- The pipeline finds its part by structure, not by line numbers: scan, match the saved layout (template), apply it, take the part with the
  saved part number, verify strictly (delimiter, column count, column names, example not moved). Any mismatch = the run fails with one
  plain sentence. A Close match is accepted only if every check passes. Several copies of the chosen table = fail.
- The generated DLL needs YamlDotNet.dll (18.1.0) next to it, plus ETLBox, ETLBox.Csv, ETLBox.Json, CsvHelper and the user's own ETLBox
  licence. It contains no licence key, writes no file and no lineage; its facts (file name, SHA-256, template, similarity, part number,
  lines, columns) are returned in the result.
- Canvas: File intake -> CSV part -> ParseValue -> DotNetObject. "Use in pipeline" in Intake Studio creates or replaces only the CSV part
  node (asks Yes/No before replacing). The selector is stored in the .etlproj as one JSON string; nothing derived is saved.
- Design-time columns come from the same reader as the run (never from the selector's names).
- Lineage events added: pipeline.part-chosen, pipeline.executed (no paths, no content).

## Differences from the plan or from what one would assume
- The CsvFile definition, JSON and hash are byte-identical to before G6 (partial methods, no Engine/Intake in CsvFile DLLs).
- `CsvFile` stays required in the definition; for a CSV part it mirrors the selector (example file = default input).
- The detector's rules are unchanged; detection quality on real files is still 64 of 93 rows matching, so the pipeline relies on the
  user's corrected layout/template.
- A part read turns ETLBox/CsvHelper delimiter detection OFF (the whole-file CsvFile read keeps it ON; pinned by a test).

## Parked (append to PARKED_G3b.md)
16. (closed in P3) clipboard test now uses an injected copy function; real clipboard test only with INTAKE_CLIPBOARD_TESTS=1.
17. (closed in P3) BackgroundRefresh_ReadsTheColumns was a test bug.
18. The selector drops trailing empty fields and writes '' for an empty header cell; ETLBox keeps them and calls them Col<N>
    (r10 parts 4 and 6, r11). Harmless because the locator compares like with like and the canvas uses the run's reader, but any new
    consumer of `location.ColumnNames` must not assume it equals the run's columns.
19. CsvFile reads keep ETLBox DetectDelimiter ON; a table with misleading lines can be read with another delimiter. Left alone to keep CsvFile behaviour.
20. DataStudioViewModelTests.C10_WithPreviewRows6000 failed once in G6d with "Assert.Null() Failure" on PreviewError, cause not found
    (the test now starts with 6,000 rows so it no longer overlaps the delayed reload; its message now shows the error text). Watch it.
21. Under a fully busy CPU, locating a part in a 12 MB test file took up to 7.3 s. Not a product limit, but note it for large real files.
22. Reading above 5,000 rows through a part is unverified (ETLBox licence expired); large-part tests use PartStream alone.
23. Data Studio Save for a CSV part writes KnownColumns that the next validation recomputes; checked only on i10.
24. Keep the laptop awake during full test runs: Modern Standby made two runs look hung (timers do not count standby time).
25. Manual checks of G5b2, G5c2, G6c and G6d in the real app are still the user's to do.
