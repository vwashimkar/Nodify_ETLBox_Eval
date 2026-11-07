# Task G4b for Claude Code: FileIntake node and Intake Studio

Goal: the user can put a File intake node on the canvas, open Intake Studio, pick a file, scan it, see the containers with their validation messages and a preview, and save the settings in the project. Read-only: the review tools (split, merge, change format) come in G5 and container nodes in G6.

## Before you start (you, not Claude Code)

1. G4a is committed on `intake`, `git status` clean, app CLOSED.
2. Fresh `claude` session in the repo; paste the block below.
3. At the end the agent writes a manual-check list into its report. You do those checks by hand, in Light AND Dark.

```
Read CLAUDE.md, docs/INTAKE_PLAN.md (sections 6, 9, 10, 15), reports/taskG4a-report.md. Then read how an existing node with a dialog is built (the ParseValue node, its view model and dialog), how nodes are registered in the palette, how project files are saved and opened (.etlproj serializer, ProjectEditor, dirty tracking), the token styles and the Light and Dark theme files, and Engine/Intake/IntakeScanService.cs, IntakePreview, ContainerDisplay and Labels from G4a.

Task G4b: FileIntake node and Intake Studio dialog. NO NEW PACKAGE. No DLL change. Do not touch: EVALUATION.md, README.md, docs/, etlbox.lic, Engine/Generation/, Engine/PipelineCore.cs, the legacy nodes, and NEVER any file under SampleData/TestData/intake/. The palette code: touch it ONLY to register the new node type the same way the existing nodes are registered; do not fix the parked double-node drag bug. Engine code has no WPF types. Keep to about 80 tool calls; commit after each numbered step that builds and passes; if you run out, stop at the end of a finished step and say exactly where.

HOW TO WORK:
- Follow the existing patterns (token styles, Thickness tokens for Margin and Padding, no Padding on panels, Mode=OneWay for read-only bindings, plain WPF windows, Nodify only for the canvas). Both themes must work: no hard-coded colours.
- Put the logic in a view model class that has no WPF window types, so it can be tested without opening a window. The window is thin.
- The original input file is never modified, moved or locked while the dialog is idle. Scanning reads it; cancel and close release it at once.
- Messages are plain sentences for a lab user: no exception names, stack lines or full paths (show the file name, and the full path only in a tooltip).

1. Node and project file (one commit).
   - A node type "File intake" (title from Labels key `node.fileintake.title`, default "File intake"; subtitle default "Reads a file and finds its parts") in the palette, in a group "Intake" (or the group name the existing code would use for a new group). It has no ports yet (container ports come in G6). On the canvas it shows the file name (or "No file chosen"), and after a scan in this session a summary such as "27 containers, 1 warning". The summary is NOT saved in the project (a rescan makes it again).
   - Settings saved in the .etlproj node: file path (relative to the project like the other paths, with the existing moved-folder handling), mode (autodetect or markers), encoding (auto, utf-8, windows-1252, iso-8859-1, utf-16le, utf-16be), and size limit in MB (default from settings, 500). formatVersion stays 1; an old project opens unchanged; unknown node types are still kept. Changing any setting marks the project dirty; scanning alone does not.
   - Run and Generate DLL: the legacy chain behaves as before. A File intake node is not part of Run yet: if the graph has one, Run shows ONE plain line "File intake nodes are not run yet." and runs the rest as before; Generate DLL ignores it and says the same line in its summary. Do not change the Generate DLL gate rules. Tests for both.
   - Tests: save and open round trip with the node and every setting; a missing file path opens with "No file chosen" and no exception; dirty tracking; the unknown-field case; a project from before this task opens and saves identically.

2. Intake Studio view model (one commit): `IntakeStudioViewModel` with the settings (file, mode, encoding, size limit), `ScanCommand`, `CancelCommand`, progress (percent, text), the list of container rows (number, display name from ContainerDisplay with Labels, lines "21 to 36", confidence, status Ok/Warning/Error, a short issue summary), the totals line ("27 containers, 0 errors, 9 warnings"), the selected container, its details (format, delimiter, header guess, skip lines, key-value form, the Reasons as sentences, the validation issues with line numbers), and its preview (IntakePreview, first 200 lines, with the "more lines not shown" and "long lines shortened" notes). Rules:
   - Scan runs on a background task through IntakeScanService; the UI thread is never blocked; progress arrives on the UI thread; Cancel stops it and the rows of an unfinished scan are NOT shown as if complete (show "Scan cancelled").
   - A new scan or a changed setting clears the old result first. Only one scan at a time.
   - With Auto encoding, if EncodingGuessed is true, show one visible notice with the encoding name and the sentence from the Reasons, and a way to pick another encoding (changing it rescans on the user's request, not at once).
   - A file over the size limit, a missing file, a locked file: the plain message from the service, shown in the dialog, no crash.
   - Tests without a window: scan i01 in both modes (rows count, totals, a SOAP-fault row has status Warning); cancel mid-scan; second scan replaces the first; a missing file; the guessed-encoding notice with a small windows-1252 file; selecting a row fills details and preview with FILE line numbers; 20 cancels in a row leave the file deletable.

3. Intake Studio window (one commit). A modal window opened by double-click on the node or a button "Open Intake Studio" in the node (follow how the ParseValue dialog is opened). Layout, following the token styles:
   - Top bar: file name with a Browse button (OpenFileDialog, all files), Mode (Auto-detect / Markers), Encoding drop-down, "Scan" and "Cancel" buttons, and a progress bar with the text under it.
   - Left (about 40 percent): Containers list, columns "#", "Part", "Lines", "Status" (a text word plus a symbol; do not rely on colour alone), with the guess confidence shown in a tooltip or a narrow column. A keyboard-friendly list (arrows, Home, End).
   - Right: a details panel on top (format, delimiter, header, skip lines, reasons, then the validation issues as lines such as "Warning, rows 97 to 101: ...") and the preview below it in a monospace font with line numbers, read-only, selectable, with horizontal scroll and no wrapping.
   - Bottom: the totals line, the guessed-encoding notice when it applies, and the buttons "Save settings" and "Close". "Save settings" writes the settings to the node and marks the project dirty only when something changed. Closing with unsaved setting changes asks once (use the existing unsaved-changes dialog style). Esc cancels a running scan, and when none runs, closes (with the same question).
   - Nothing in this window edits containers. Do not add split, merge or change-format buttons; put a one-line grey note "Review tools come later" only if the layout looks empty without it.
   - Resizable with a sensible minimum size; no clipping at 125 and 150 percent display scale (say how you checked it, or mark [U]).
   - Check the window in BOTH themes by building a small screenshot test if the existing tests have such a harness; if they do not, say so and do not invent one.

4. Docs and config. Create the default labels file on first use of the node (`Labels.WriteDefaultFile`, only when the file does not exist) and read labels once at startup; a bad labels file never stops the app. Update the Current state in CLAUDE.md (the only doc you may edit).

Build, run dotnet test with --blame-hang-timeout 60s (exact summary line, three runs in a row) and the legacy --run-tests (baseline 43 pass, 5 fail, 4 observed), using the normal commands (the app is closed). Write reports/taskG4b-report.md: what changed per step; test names and results; every rule you chose that this task does not state (mark [U]); "Overfit risk"; what you could not verify; and this manual-check list for the user to do by hand, copied exactly:

MANUAL CHECKS (Light and Dark, do each in both):
 1. Drag a File intake node from the palette: one node appears (not two). Its title is "File intake" and it says "No file chosen".
 2. Double-click it: Intake Studio opens. Browse to intake_test_data i01_sample.txt, mode Auto-detect, press Scan: the progress bar moves, the list fills (27 containers), the totals line shows 9 warnings.
 3. Click a container: details and preview change; the preview line numbers match the file lines (a container starting at line 40 shows 40 first).
 4. Switch Mode to Markers and scan again: 19 containers.
 5. Make a Windows-1252 test file in PowerShell: [IO.File]::WriteAllBytes("$env:TEMP\w1252.csv", [byte[]](0x4E,0x61,0x6D,0x65,0x2C,0x43,0x6F,0x6E,0x63,0x20,0x28,0xB5,0x67,0x2F,0x6D,0x4C,0x29,0x0D,0x0A,0x41,0x2C,0x31,0x2E,0x35,0x0D,0x0A)) . Browse to it with Auto encoding: the notice says the file is read as Windows-1252 (a guess) and the preview shows "Conc (µg/mL)". Then set the encoding to UTF-8 and scan again: a warning says 1 character could not be read correctly. Then scan real/r09_qiacuity_dpcr.csv in Auto: NO encoding notice (it is valid UTF-8), and the warning about 2 characters that could not be read correctly stays, because the file itself contains them.
 6. Scan the large generated file (or any file over 100 MB), press Cancel after a second: "Scan cancelled", no rows shown, the file can be deleted in Explorer at once.
 7. Pick a file bigger than the limit: a plain message names the limit in MB.
 8. Save settings, close, save the project, reopen it: the file, mode and encoding are still set; a moved project folder still finds the file.
 9. Press Run on a graph with the File intake node and the old nodes: one plain line says File intake nodes are not run yet, the rest runs as before.
10. Close the dialog with unsaved changes: it asks once. Esc during a scan cancels it.
11. At 125 and 150 percent Windows scale: nothing is cut off.
12. Rename the node title in %AppData%\EtlDesigner\labels.txt, restart: the new title shows. Put garbage in the file: the app still starts.

Reply in at most 5 lines.
```

## What you do after it finishes

Send me `reports/taskG4b-report.md`, and do the manual checks (screenshots of checks 2, 5 and 10 in Dark are enough). Then G5 (review tools and layout templates).
