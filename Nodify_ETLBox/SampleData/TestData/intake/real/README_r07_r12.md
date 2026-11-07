# Real instrument samples r07 to r12 (the fresh hold-out for G2b)

Source: test-data files of Benchling-Open-Source/allotropy (MIT licence, (c) 2023 Benchling, Inc.), folder
tests/parsers/<vendor>/testdata/. Copied byte for byte. Put them next to r01 to r06 in
`SampleData/TestData/intake/real/`, and the truth files next to the others in `real/expected/`.

| File | Instrument / software | Encoding, line ends | What is new or hard |
|---|---|---|---|
| r07_ctl_immunospot.txt | CTL ImmunoSpot analyzer | UTF-8 (one odd character), CRLF | `Key: value` lines mixed with sentences; five whitespace-aligned plate grids (spaces, not a delimiter) with a blank line between header and rows |
| r08_revvity_kaleido.csv | Revvity Kaleido | UTF-8, CRLF, non-breaking spaces | grid with a title and a line of `Key: value` pairs above it; `Key:,,value` rows; sections with single blank lines inside; Platemap title right after key-value lines |
| r09_qiacuity_dpcr.csv | QIAcuity dPCR | UTF-8, LF, no final line end | `sep=,` hint line, then one fully quoted 16-field table (the easy case) |
| r10_methodical_mind.txt | Methodical Mind plate reader | ASCII, CRLF | NO blank lines at all: sections start with `==========Name====` separator lines; label rows `Name :<TAB>values` |
| r11_roche_cedex_bioht.txt | Roche Cedex Bio HT | ASCII, LF | tab table with no header, a header record as the first row, padded values, trailing tabs |
| r12_msd_workbench.csv | MSD Workbench | ASCII, LF | one title line plus a 16-field table with many empty cells and NaN (the easy case) |

Truth files (`expected/r07_truth.csv` to `r12_truth.csv`) were written by hand from the files alone, before any
G2b rule existed. Same columns and rules as r01 to r06, with two additions:
- `truth_format` may be `fixedwidth` (a table whose columns are separated by runs of spaces, no delimiter; delimiter value `space`).
- `skip_lines` counts every line above the header inside the region, blank lines included (r07, r08).
