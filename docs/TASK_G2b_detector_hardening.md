# Task G2b for Claude Code: harden the detector, test on fresh real files

G2 gave 42 of 56 on the files it was built with and 3 of 9 on its hold-out. The hold-out misses showed general weaknesses. This task fixes those, and measures again on six NEW real files that nobody has tuned on.

## Before you start (you, not Claude Code)

1. G2 is committed on `intake`, `git status` clean, app closed.
2. Unzip `real_instrument_samples_r07_r12.zip` INTO `Nodify_ETLBox/SampleData/TestData/intake/` (it contains a folder `real` with `r07` to `r12` and `real/expected/r07_truth.csv` to `r12_truth.csv`; let it merge with the existing `real` folder). Do not move or rename anything.
3. `git add` the new files, check `git ls-files --eol Nodify_ETLBox/SampleData/TestData/intake/real` (no conversion; the existing `real/** -text` rule covers them), then `git commit -m "Add real samples r07-r12 and truth (fresh hold-out)"`.
4. Fresh `claude` session in the repo; paste the block below.

```
Read CLAUDE.md, docs/INTAKE_PLAN.md (sections 4, 13, 14), reports/taskG2-report.md and reports/g2-detect-report.md. Then read Engine/Intake/AutoDetector.cs, RowShape.cs, StructureTrackers.cs, DetectorSettings and the tests AutoDetectTruthTests.cs and AutoDetectorRuleTests.cs.

Task G2b: improve the detector on general weaknesses and measure on fresh files. No UI, no DLL change, no new package. Do not touch: EVALUATION.md, README.md, docs/, etlbox.lic, Engine/Generation/, the legacy nodes, Run, Engine/PipelineCore.cs, the palette code, and NEVER any file under SampleData/TestData/intake/ (data, expected/ and real/ are read-only ground truth). Engine code has no WPF types, no assembly attributes and no test-only code. Keep to about 70 tool calls; commit after each numbered step that builds and passes; if you run out, stop at the end of a finished step and say exactly where.

HOW TO WORK:
- Development set: i08, i09, i10 and real r01 to r06 (r05 and r06 are development files now: their hold-out status is gone because G2 reported their results). FRESH HOLD-OUT: real r07 to r12. While you work do not open r07 to r12 or their truth files and do not run any test on them: exclude them from the normal run (reuse the HoldOut test; point it at r07 to r12, run only with INTAKE_HOLDOUT=1). They are run ONCE at the end of step 6 and reported separately. If you change a rule after seeing them, say so in the report.
- Every rule below is a general rule. No file names, vendor names or strings from the test files in the detector. Each rule gets unit tests with tiny in-memory inputs that you write yourself (not copied from the real files).
- Tier A (i08, i09, i10) must keep passing: if a change breaks a Tier A row, undo the change and say so. If a Tier B truth row disagrees with a rule, do not stop: keep the rule, list the row in the report with the reason.
- Detector thresholds stay in DetectorSettings (data, not constants).

1. Trailing empty fields. When comparing field counts and when deciding what a line is, ignore trailing empty fields (a line "a,b,,,," has 2 fields). A line made only of delimiters or `""` stays a weak line. A `Key: value, Key: value` line padded with commas to the table width must count as the Pairs form of key-value. Unit tests for both.

2. Ragged right. A table may have rows SHORTER than the header (or than the first row) when the missing fields are trailing and every row has at least `MinRaggedFields` (default 2) fields; the confidence is Likely at best, with a Reason "rows shorter than header". Rows longer than the header are covered by the existing tolerance. Unit tests: a 10-field header with rows of 2 and 3 fields is one table; a block where every line has a different field count is not.

3. Key-value against a table. A block of at least `MinKeyValueLines` lines with the same field count that has NO header guess, where the first column holds only distinct, non-numeric labels and the other columns are not all numeric, is key-value (Likely, Reason "distinct labels in first column, no header"), also with 2 fields. A real header row or a repeated or numeric first column keeps it a table. Replace the "table wins when both reach the end" rule of G2 with this one and say so in the report. Unit tests both ways (a 2-column table with a header and numbers stays a table; "Name<TAB>value" lines become key-value).

4. Weak lines above a table. A weak line (`""`, `,,,`) directly above a table, or between the title lines and the table, is a skip line of that table (inside its region). Weak lines at the other edges stay as in G2.

5. Section separator lines. A line of 5 or more of the same punctuation character (`=`, `-`, `#`, `*`, `~`, `_`), optionally with a name in the middle (`====Data====`), is a separator: it ends the region before it, is the first skip line of the region after it, and its name (when there is one) is that region's name. Two separators in a row: the last one is the title. Unit tests, including a file with no blank lines at all.

6. Whitespace-aligned tables, a new format `fixedwidth`. At least 3 consecutive rows (blank lines inside the region are NOT allowed in this stage) with the same number of whitespace-separated tokens (at least 4), where in at least half of the columns after the first the tokens are numeric, are a `fixedwidth` region with Delimiter "space", Likely; a first row of tokens that are all non-numeric or all consecutive integers is a header (HeaderGuess Guessed). Add `fixedwidth` to the format lists in the model and tests, with one unit test for each of: a plate grid, prose (must NOT become fixedwidth), a table with a title above. This is a new format name only: nothing downstream uses it yet.

7. Small settings: MaxKeyLength 60; MaxValidationMb 16. Update the tests that name the old values.

8. Run the development set and write the scorecard to reports/g2b-detect-report.md in the same format as g2-detect-report.md, with the G2 numbers next to the new ones per file. Compare Tier A (must be unchanged and passing).

9. Hold-out: after step 8 is committed, run INTAKE_HOLDOUT=1 on r07 to r12 once, append the table (per file: Match, LinesOff, FormatOff, Merged, Split, Missed; overall) to the report under "Fresh hold-out", and do not change any rule afterwards. For every non-Match say in one sentence why the detector went wrong (do not fix it).

Also: update RealIntakeFileTests with the six new sizes (r07 4932, r08 3790, r09 3093, r10 3893, r11 1849, r12 4052); the property test (every non-blank line in exactly one container) must pass on all twelve real files and all the other test files.

Build, run dotnet test with --blame-hang-timeout 60s (exact summary line, three runs in a row) and the legacy --run-tests (baseline 43 pass, 5 fail, 4 observed). Update the Current state in CLAUDE.md (the only doc you may edit). Write reports/taskG2b-report.md: what changed per step; test names and results; the development scorecard before and after; the fresh hold-out table; per non-match reasons; the rules you chose that this task does not state (mark [U]); "Overfit risk" (any rule that exists for one file, and any rule whose settings you tuned while looking at a specific file); every Tier B truth disagreement; what you could not verify. Reply in at most 5 lines.
```

## What you do after it finishes

Send me `reports/taskG2b-report.md` and `reports/g2b-detect-report.md`. I read the fresh hold-out table against the raw files. Then G3 (validation) and G4 (the FileIntake node and Intake Studio).
