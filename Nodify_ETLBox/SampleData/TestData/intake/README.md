# Intake test data (multi-format files)

Ground truth for the Intake stage. Every `iNN_*.txt` file has an expected result in `expected/`.
The expected files were produced by a small reference scanner that implements the rules in the plan
(section "Marker rules"); they were NOT produced by the code under test.

| File | What it tests |
|---|---|
| i01_sample.txt | The user's real sample: 18 marker segments + 2 plain-text blocks (intro, trailing). The intro contains two marker-looking lines (lines 5 and 7: indented, and `<format>` is not a name) that must stay plain text. |
| i02_crlf.txt | Same as i01 with CRLF line endings: same segments, same line numbers, different byte offsets. |
| i03_bom.txt | Same as i01 with a UTF-8 BOM: byte offsets start after the BOM (+3). |
| i04_unterminated_last.txt | The last BEGIN has no END: the segment runs to the end of the file, status Error. |
| i05_missing_end_before_next_begin.txt | json-object has no END before the next BEGIN: it is closed at the next BEGIN, status Warning. |
| i06_stray_end.txt | An END with no BEGIN stays plain text and gives a file-level warning (see i06_stray_end_warnings.csv). |
| i07_edge_cases.txt | Missing name, missing notes, empty segment, unknown format, indented and lowercase marker lines (stay text), a trailing tab and trailing spaces that must be preserved, END with trailing spaces, trailing blank lines. |
| i08a..f_nomarkers_*.txt | Files without markers: Auto-detect must name one format for the whole file (i08_detected_formats.csv). |

| i09_sample_nomarkers.txt | **Real-instrument case.** The same content as i01 with every BEGIN/END marker line removed (blank lines kept). The auto-detector must find the same blocks. Truth in `expected/i09_sample_nomarkers_truth.csv`. |
| i10_instrument_header_table_footer.txt | A synthetic instrument export with no markers: 20 `Key: value` header lines, a blank line, a CSV table with a header, a blank line, 2 footer lines. Truth in `expected/i10_instrument_truth.csv`. Real instrument files will replace this when the user supplies them. |

Truth files for i09 and i10 (columns: order, truth_format, truth_name, first_line, last_line, line_count, detectability, tier, note):
- **Tier A** blocks must be found with the right format and exact first and last line. **Tier B** blocks are best effort: the detector's result is REPORTED (right, wrong, merged, split) but a miss does not fail the build; each B row says why it is hard.
- detectability (Strong, Medium, Weak) says how clear the content signature is. A format guess below Strong is shown to the user as Guessed.
- The detector reports `csv`, `tsv`, `json`, `json-lines`, `xml`, `soap`, `yaml`, `keyvalue`, `text`. BadgerFish (a kind of json) and `soap` (a kind of xml) are sub-types and count as correct when the base format is right and the sub-type is a Guessed hint.

Columns of `*_segments.csv`: order (1-based, text blocks count), kind (marker or gap_text), format, name, notes,
marker_begin_line, marker_end_line (empty when there is none), content_first_line, content_last_line,
content_line_count, content_start_byte (offset of the first content line, counted from the start of the file
including a BOM), content_end_byte (offset just after the last content character, before its line ending),
status (Ok, Warning, Error), message.
Line numbers are 1-based and count every line of the file.

Large-file test (generate at test time, do not store): the text of i01 repeated N times, joined directly.
Expected: 18 marker segments per copy, and N+1 text blocks (the trailing text of one copy and the intro of the
next copy form ONE block). So segments = 18*N + N + 1.
