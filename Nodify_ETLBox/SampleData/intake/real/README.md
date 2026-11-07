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
