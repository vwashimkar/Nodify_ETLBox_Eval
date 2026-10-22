# Spike test data

Test files for the ETLBox spike (CSV -> filter by country -> JSON -> parse -> List<Dictionary<string,string>>).
Copy this folder to `SampleData/TestData/` in the project and set **Copy to Output Directory = Copy if newer** for all of it.

## Files
| File | Purpose |
|---|---|
| `expected_results.csv` | One row per test (file, delimiter, country) with the expected counts |
| `expected_values.json` | Exact expected Filtered/Parsed rows for the 41 small tests (key = test_id) |
| `00_` to `19_*.csv/tsv` | The inputs (see `what_it_tests` in the manifest) |

`00_original_employee_list.csv` is your original sample. `employee_list_1.csv` was byte-identical to it.

## The rules the expectations follow (the spec)
1. Country = text before the FIRST `|`, trimmed. Compare ordinal, ignore case. Equality only (`Indiana` does not match `India`).
2. Accents matter: `México` and `Mexico` are different countries.
3. A row is **malformed** if it has no `|`, or the country part is empty after trimming. Malformed rows are skipped, reported once as a count, and never crash the run. They are counted whatever country is chosen.
4. Every value is a string. Nothing is parsed as a number or date. Empty cells stay `""`.
5. Filtered JSON keeps ALL columns of the file (8 normally, 10 for file 08). Parsed JSON has exactly `first_name`, `last_name`, `email`.
6. No matches gives `[]` in both outputs and no error.

## Expectation types
- `strict`: counts, ids (in order, via SHA-1 of the comma-joined id list) and values must match exactly.
- `observe`: the behaviour is not defined by our spec. The expected numbers are the IDEAL. Record what really happens as a finding in EVALUATION.md.
- `error_expected`: the run must stop with a clear message that names the problem. No crash, no hang, no partial output.

## Delimiter column
`,` `;` or `\t` (a backslash and a t, meaning the tab character).

## Notes
- Volume files (18, 19) are for the ETLBox free-edition limit (5,000 rows per DataFlow). File 18 has 12,000 rows in but only 3,000 India out; file 19 has exactly 5,001 India out. Germany in file 18 is exactly 5,000.
- Emails in file 03 contain odd characters on purpose.
- Expected values were computed with Python's `csv` module from the written files, not from the pipeline, so a mismatch is a pipeline finding.
