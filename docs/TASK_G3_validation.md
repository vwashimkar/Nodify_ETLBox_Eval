# Task G3 for Claude Code: validation per container, YAML detection, sub-types

Goal: after the detector has found containers, the tool checks each one for its format and says in plain language what is wrong and where. YAML joins the detector (YamlDotNet is approved for this). SOAP and BadgerFish become proper sub-types. Still no UI (that is G4): the results are engine data plus a report that G4 will show.

## Before you start (you, not Claude Code)

1. `intake` merged into master and back (`git switch intake`, `git merge master`), `git status` clean, app CLOSED (the last run could not build normally because the app locked the exe).
2. Fresh `claude` session in the repo; paste the block below.
3. Note: r07 to r12 are now development files (their results were seen). There is no hold-out in this task: validation is rule-based and tested with small samples written for the task.

```
Read CLAUDE.md, docs/INTAKE_PLAN.md (sections 4, 7, 8, 13, 14), reports/taskG2b-report.md and reports/g2b-detect-report.md. Then read Engine/Intake/*.cs (model, AutoDetector, RowShape, StructureTrackers, IntakeReader, DetectorSettings) and the tests AutoDetectTruthTests.cs and AutoDetectorRuleTests.cs.

Task G3: validation, YAML, sub-types. No UI, no DLL change. NEW PACKAGE ALLOWED, ONLY THIS ONE: YamlDotNet (MIT), for YAML code only. Pick the newest stable release that is at least two weeks old, put it in Nodify_ETLBox.csproj only (not in the test project unless a test needs it), say the version in the report, and update the package rule in CLAUDE.md (the only doc you may edit): "YamlDotNet is approved for YAML code only; any other package needs an explicit OK". Do not touch: EVALUATION.md, README.md, docs/, etlbox.lic, Engine/Generation/, the legacy nodes, Run, Engine/PipelineCore.cs, the palette code, and NEVER any file under SampleData/TestData/intake/ (data, expected/ and real/ are read-only). Engine code has no WPF types, no assembly attributes and no test-only code. Keep to about 70 tool calls; commit after each numbered step that builds and passes; if you run out, stop at the end of a finished step and say exactly where.

HOW TO WORK: general rules only (no file names, vendor names or strings from the test files in the code); thresholds and limits in DetectorSettings or a new ValidationSettings (data, not constants); every rule has unit tests with small inputs you write yourself. Tier A (i08, i09, i10) must keep passing; if a change breaks one, undo the change and say so. Tier B and the real files never fail the build; their results go in a report.

0. Small detector fix first (one commit). A csv or tsv region whose first row has an empty first cell and whose other cells are consecutive integers (1..n or 0..n-1) has HeaderGuess Yes (a plate grid header), as the fixedwidth rule already does. Unit tests. Re-run the development scorecard (i09 tier B, r01 to r12; r07 to r12 are development files now) and put before and after counts in the report. Tier A unchanged.

1. Model (Engine/Intake/): `ValidationIssue` (severity Info, Warning or Error; a short code such as "json.syntax"; a plain-language message of one sentence that a lab user can read; first line number and optional column in the FILE (1-based, whole-file numbering); optional line count), `ContainerValidation` (container order, status Ok, Warning or Error = the worst issue, issues list capped at `MaxIssuesPerContainer` (default 20) with a total count and a "more not shown" flag), `IntakeValidationResult` (one ContainerValidation per container, totals, elapsed ms, cancelled, error). JSON serialization with enums as strings and round-trip tests. The message text never contains a stack trace, a .NET exception name or a full path.

2. Validator (Engine/Intake/IntakeValidator.cs): `Validate(IntakeItem, IntakeResult, IProgress<IntakeProgress>, CancellationToken)`. For each container it re-reads the container's lines by offsets (never the whole file, never more than `MaxValidationMb` per container: a larger container is checked on its first part only and gets a Warning "checked only the first N MB"). Progress phase "Validating (n of m containers)" about every 250 ms, cancel checked often with the file released at once, no exception for a missing or locked file (an Error in the result). Checks per format:
   - json: System.Text.Json (JsonDocument, MaxDepth from settings, default 64). A syntax error gives the line and column inside the FILE and a one-sentence reason. Duplicate property names are a Warning.
   - json-lines: every line must be one JSON object; list up to the cap of bad lines with their numbers.
   - xml: XmlReader with DtdProcessing Prohibit, XmlResolver null, a character limit from settings. Well-formedness errors with line and column. A DOCTYPE is an Error "DOCTYPE declarations are not read" and the container is not parsed further. A SOAP envelope whose Body holds a Fault is a Warning "SOAP fault: <faultstring>" (read it safely, cut to 200 characters).
   - csv and tsv: with the container's own Delimiter, SkipLines and HeaderGuess: quote-aware field counting; rows whose count differs from the header or the first data row (after the same trailing-empty rule the detector uses) are listed with line numbers; an unterminated quote is an Error; duplicate or empty header names are Warnings; a container with zero data rows is a Warning.
   - keyvalue: duplicate keys (Warning, with both line numbers), a key with no value (Info), lines that do not fit the container's KeyValueForm (Warning).
   - yaml (new): YamlDotNet in parse-only mode (YamlStream or the parser; NEVER deserialize into objects, never resolve custom tags or types). Syntax errors with line and column; a maximum node count and nesting depth from settings so a hostile file cannot exhaust memory; alias use is allowed but the number of alias references is capped.
   - text: no format check. Every format: if the container's text contains the Unicode replacement character or NUL, a Warning "N characters could not be read correctly (wrong encoding?)" with the first line.
   Unknown or future format names: Info "no checks for this format yet".

3. YAML in the detector (Engine/Intake/AutoDetector.cs). A block is `yaml` only when it has real YAML structure (at least one of: indentation-based nesting under a `key:` line, `- ` list items, an anchor/alias, a `|` or `>` block scalar, a `---` document marker) AND the YamlDotNet parser accepts it. Otherwise it stays key-value or text as before. Confidence Likely (Sure only with `---` or a `%YAML` header). It is tried after JSON, XML, table and key-value rules decide nothing else: do NOT change the order in a way that breaks a Tier A row; the Tier B row i09 16 is expected to become a Match, report it. False-positive tests you write yourself: prose that contains colons, a key-value block (r04's style, `* Name = value`, `Name: value` lines with no nesting), a log excerpt, a CSV with a colon in a cell: none may become yaml. The real files must not change format because of this step except where the report says so (compare the scorecard before and after).

4. Sub-types. `SubType` stays the place for them; add a `Reasons` entry saying why.
   - soap: Envelope root in the SOAP 1.1 or 1.2 namespace (already done in G2: keep, and put the version 1.1 or 1.2 in a new `SubTypeVersion` field).
   - badgerfish (a json sub-type, always Guessed): an object whose keys include `@`-prefixed attributes or `$` text keys in at least `BadgerFishMinHits` (default 2) places. Detection reads the already parsed JSON, not the text, with a depth and node limit.
   The i09 truth says BadgerFish counts as correct when the base format json is right: do not change the base format.

5. Tests (xUnit; slow things under TestTimeout):
   a. Valid and invalid samples for EVERY format in step 2, written by you as small in-memory strings: the status, the code, the line number and that the message has no exception text. At least: JSON with a missing comma on line 3 of a container that starts at file line 40 (the issue says line 42); XML with a mismatched tag; XML with a DOCTYPE; a SOAP fault; csv with a ragged row, an unterminated quote and a duplicate header; keyvalue duplicate key; YAML with a bad indent; YAML with a huge alias fan-out (must stop at the limit, not hang or use much memory); JSON nested 200 deep (Error, no stack overflow); a text container with a replacement character.
   b. The 20-issue cap and the "more not shown" flag; a container larger than MaxValidationMb.
   c. Run the validator over ALL test files (i01 to i10, r01 to r12) in AutoDetect mode (and i01 to i07 in Markers mode): no exception, every container has a status, the property check from G2 still holds, and write reports/g3-validation-report.md with, per file, each container's status and its issues (code, line, message). The test never fails on Warnings or Errors in real files; it fails only on an exception or a missing status.
   d. Cancel 20 times during validation of the large stress file (generated at test time, N = 2,000) with the file deleted afterwards; progress is reported; the last report equals the totals; memory stays flat (compare N = 200 and N = 2,000, report numbers).
   e. YAML detection unit tests and the false-positive tests of step 3; sub-type tests of step 4.
   f. Marker mode tests and all G2 tests still pass.

Build, run dotnet test with --blame-hang-timeout 60s (exact summary line, three runs in a row) and the legacy --run-tests (baseline 43 pass, 5 fail, 4 observed), using the normal commands (the app is closed). Write reports/taskG3-report.md: what changed per step; the YamlDotNet version and how YAML is kept safe; test names and results; the scorecard before and after step 0 and step 3; the validation report summary per real file (what it found, and whether each issue is a true problem in the file or a false alarm, in one sentence each); the large-file numbers; every rule you chose that this task does not state (mark [U]); "Overfit risk"; what you could not verify. Reply in at most 5 lines.
```

## What you do after it finishes

Send me `reports/taskG3-report.md` and `reports/g3-validation-report.md`. I check the validation messages against the raw files, because the wording is what a lab user will read in G4.
