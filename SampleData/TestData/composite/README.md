# Composite-cell test data

Made-up data for the CSV workflow spec. Nothing here comes from a real file.

- `expected_results.csv` lists every test, the settings to use, and the expected counts.
- `expected/Cxx_output.csv` is the expected long-form output. `_rejects.csv` and `_flagged.csv` list rejected and flagged records by record number. Rejects also give the file line where the record starts (it differs from the record number when a quoted cell contains a line break, and it counts skipped lines).
- **slots**: the mapping from component position to value columns, stored by column name. `auto(r01..r18;3)` means the named columns in order, cut into groups of 3. `S1=m01,m02;S2=m03,...` is an explicit mapping; slots may differ in size and may skip columns. A record may have fewer components than slots; only the slots it needs are used.
- **excess_policy**: what to do when a record has more components than slots: `reject`, or `pair what fits` (flagged).
- **duplicate_policy**: `allow` (default) or `flag`.
- **carry_columns**: `none`, a list of column names, or `*` (every column that is not a value column or the source cell). Carried columns come first in the output, under their own names. `Col1`, `Col2`, ... are the generated names when there is no header.
- **delimiters**: `L1=:(once)` splits at the first `:`; `L2=|(all)` splits every `|`; `L3=/(each)` splits each component at the first `/`.
- Output order is input record order, then component order. Value columns are named value_a, value_b, ...; a slot with fewer columns leaves the rest empty.
- JSON tests (C15, C16) are compared as parsed JSON. Flat: one object per output row, all values as text. Nested: one object per input record, carried columns on the record, a `components` array whose objects hold only that slot's own columns.
- C10 is checked by counts and the SHA-1 in `expected_sha1`. It needs an ETLBox licence.
- C14 depends on skipping lines before the header; whether ETLBox supports that is unverified.
