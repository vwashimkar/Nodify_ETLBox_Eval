# Notes to append to docs/INTAKE_PLAN.md (you append them yourself; agents do not touch docs/)

## Decisions made during G5a to G5c1 (2026-11-08 to 2026-11-10)

1. **Edits are stored as the final layout ("plan") of one file**, bound to the file's SHA-256 and size. If the file changes, the plan is not applied and the user is told in one sentence. Format version 2 stores only what the user decided per part (range, format, options, name, confirmed) plus a small `fix` where the rebuild would differ; version 1 plans still load.
2. **Lineage is a JSON-lines text file `lineage.log` in the folder of the exe** for now (fallback `%AppData%\EtlDesigner`). It holds event, id, UTC time, intake id, file NAME only, SHA-256 and counts. No folder paths, user name, machine name or file content. Rotation at 10 MB. Later it may move to a database behind `ILineageLog`.
3. **Templates are matched by STRUCTURE only** (ordered part signatures: format, delimiter, column count, header; repeats collapsed), never by file name, size, hash or path. Matching is run on the detector's automatic layout of the new file. A template is never applied silently: the Studio shows a bar and the user decides. Applying is one undoable step. Column names are a second check (G5c2): a structural match with different column names is only "Close".
4. **Templates are stored one JSON file per template** in a `templates` folder next to the exe (fallback `%AppData%\EtlDesigner\templates`), written atomically. A template may contain short anchor lines and column names from the file; the lineage never does.

## Differences from section 6 of this plan (found in G5c1)

1. The plan says templates go through `IConfigStore`; G5c1 built `ITemplateStore` / `FileTemplateStore`. `IConfigStore` is still unused.
2. The plan describes regions by how they start and end (anchor, field count, footer pattern, fixed line count). G5c1 places template parts on the automatic layout by structure and uses anchors only for splits. There is no "ends at" rule.
3. The plan says a mismatch shows "Layout mismatch" and offers Auto-detect. Now a non-matching template is refused with "The saved layout 'x' does not fit this file, so nothing was changed." and the automatic result stays; parts that cannot be placed are listed as skipped.
4. The plan says the template becomes part of the pipeline definition and the DLL. Not done yet (G7).
5. The plan says every edit updates the template. Now a template is saved only when the user presses Save as template.

## Parked (append to PARKED_G3b.md)

10. `DataStudioViewModelTests.C10_WithPreviewRows6000...` failed once (`Expected 1000, Actual 0`), never reproduced in 73 runs. Suspected cause: the delayed reload (400 ms after setting PreviewRows) cancels the test's own reload under load.
11. `ExecuteViewModelTests.C10_WithoutAValidLicence...` failed once (`Expected 200, Actual 0`): expired ETLBox licence gives an early stop. Fixed in G5c2 step 0.
12. A file that is one single table (r09 Qiacuity, r12 MSD Workbench) cannot be told apart by structure alone; G5c2 adds the column-name check.
13. A saved version 2 layout is rebuilt with the detector: if detector rules change later, derived fields (confidence, reasons) may differ; ranges, formats, options and names do not.
14. Edits run on the UI thread; a split of a very large part (up to 64 MB) may pause the window briefly.
15. Preview lines can no longer be selected as text (list instead of text box); Ctrl+C and "Copy line" copy one line.
