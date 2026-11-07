## G3b: parked items (append to docs/INTAKE_PLAN.md, section 16 or at the end)

Found by the G3 validation report (2026-11-08). Parked on purpose: fix them when new real files are available as a hold-out, or when the G5 review tools exist.

1. Marker segments with a preamble: in Markers mode a segment has no skip lines, so `#` preamble lines count as table rows and give false `csv.field-count` warnings (i01 container 13). Remedy: G5 lets the user set skip lines and the header by hand.
2. In AutoDetect mode a long marker line (over 60 characters) is taken as the first row of the table below it (i01 containers 10 and 17). Real instrument files have no markers, so low priority.
3. Title lines taken as the first data row: a table's 2-field title line is counted as a data row, so messages say "first data row has 2" (r01 containers 12 to 15 and 17).
4. A `Key:,,value` block is taken as a table with a header (r08 line 35).
5. XML parser messages are shown as the parser says them (for example "Data at the root level is invalid"). Map the common ones to plain sentences. In a marker file the usual cause is a missing END marker.
6. JSON error columns are byte positions; they should be character positions (matters for lines with a micro sign or other non-ASCII text).
7. `ProjectEditorTests.BackgroundRefresh_WithAMissingFile_GivesTheNormalMessage_AndDoesNotThrow` failed once in a full run and passed on repeats. Diagnose as a timing race (second flaky test, after the ETLBox row-loss race).
8. Detector gaps from G2b: a single blank line inside a table or section cannot be told from two tables (r02, r07, r08); r10 row 1; r05 row 4.
9. Wording of the bad-character warning: when the file is valid UTF-8 and already contains the replacement character (r01, r07, r09 do), "could not be read correctly (wrong encoding?)" misleads, because no encoding fixes it. Say instead: "N characters in this part were already damaged in the file (shown as the replacement character); the original export may be in another encoding." Keep the old wording only when the file is read with an explicit encoding and invalid bytes were met.
