# Real instrument export samples (r01 to r06)

Source: test-data files of the open-source project Benchling-Open-Source/allotropy
(https://github.com/Benchling-Open-Source/allotropy, MIT licence, (c) 2023 Benchling, Inc.),
folder tests/parsers/<vendor>/testdata/. Copied byte for byte, not edited.
They are example exports from the vendors' software; some may have been anonymised or
trimmed by that project. They are NOT tiamo/Metrohm files (none exist in that repository).

| File | Instrument / software | Encoding, line ends | Structure |
|---|---|---|---|
| r01_luminex_xponent.csv | Luminex FlexMAP 3D, xPONENT 4.3 | UTF-8, CRLF | quoted key,value header lines (some with several values), blank-line separated sections, tables |
| r02_moldev_softmax_pro_utf16.txt | Molecular Devices SoftMax Pro | UTF-16 LE with BOM, CRLF | `##BLOCKS= n`, `Plate:` blocks with a tab table, `~End` markers, very wide rows |
| r03_perkinelmer_envision.csv | PerkinElmer EnVision | ASCII, LF | repeated groups: "Plate information" table, "Background information" table, 8x12 plate grid |
| r04_quantstudio.txt | Applied Biosystems QuantStudio 7 Flex | ASCII, CRLF | `* key = value` header, then `[Section]` titles each followed by a tab table |
| r05_agilent_gen5.txt | Agilent BioTek Gen5 (Synergy Neo2) | ASCII, LF | tab-separated key/value lines, procedure text, then data blocks |
| r06_bmg_pherastar.csv | BMG LABTECH PHERAstar | ASCII, LF | `key: value, key: value` header lines in a padded CSV, a plate grid with empty cells |

Expected results (truth CSVs) are NOT included. They are written by hand after looking at each file.

## Truth files (expected/rNN_truth.csv)

Written by hand from the files, not by any scanner or by the code under test. Every non-blank line of each
file belongs to exactly one row (checked by a script). All rows are Tier B: the detector's result is
reported (match, wrong format, merged, split) and does not fail the build.

Columns: order, truth_format, truth_name, first_line, last_line, line_count, detectability, tier,
skip_lines (preamble lines at the top of the region that belong to its title, not to the table),
header (yes/no/n/a: whether the first line after the skip lines is a header), delimiter (`TAB` for tab),
accept_also (another format that also counts as right), note.
Line numbers are 1-based and count decoded lines (r02 is UTF-16: decode first).
Region rules used when writing the truth: a title line directly above a table is part of it as a skip line;
one blank line does not split text; a blank line ends a table; the first line of a region and the last are never blank.

Things these files show that the first plan did not cover (G2 must handle or report):
1. Key-value lines other than `Name: value`: quoted `"Key","value"` rows (r01), `* Name = value` (r04),
   `Key<TAB>value` (r05), `Key: ,,,,value` (r03), and several `Key: value` pairs on one line (r06).
2. Tables whose field count varies by one or more (r01 key rows, r03 grids, r04 rows longer than the header,
   r05 rows shorter than the header).
3. Tables with a title above them and a different header directly after another table with no blank line (r01).
4. Lines that look blank but are not (commas only in r06, `""` in r01).
5. One block that contains single blank lines inside (r02 plate block).
6. UTF-16 (r02).
