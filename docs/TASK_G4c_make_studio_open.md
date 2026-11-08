# Task G4c for Claude Code: make Intake Studio open, and prove it with tests that load the window

Problem (seen in two screen recordings, user's machine, Debug build from Visual Studio): in the File intake node nothing opens Intake Studio. The node's settings panel shows only File path, Mode, Encoding and Size limit (no "Open Intake Studio" button). The canvas node shows "Idle | rows: 0 / Configured" and never the file name or "No file chosen". The "..." file picker only offers `CSV files (*.csv)`, so `.txt` test files cannot be picked. All G4b tests passed because none of them loaded the window XAML or ran the real open path.

## Before you start (you, not Claude Code)

1. G4b is committed on `intake`, `git status` clean, app CLOSED (stop the debugger in Visual Studio too).
2. If a file `unhandled-exception.log` exists (the app writes it; the agent knows where), send it to me too. Do not delete it before the agent has read it.
3. Fresh `claude` session in the repo; paste the block below.

```
Read CLAUDE.md, reports/taskG4b-report.md. Then read: how the File intake node is created (NodeFactory.CreateFileIntake), the node view XAML or data templates that draw nodes on the canvas and the settings panel (how ParseValue shows its Configure button and how its dialog opens), MainViewModel.ShowIntakeStudio, DoubleClickCommand, UI/IntakeStudioWindow.xaml, UI/ViewModels/IntakeStudioViewModel.cs, the App.xaml resource dictionaries (Light and Dark themes, DialogStyles.xaml) and how themes are switched.

Task G4c: make Intake Studio open for real, fix the node text and the file filter, and add tests that load the real window. NO NEW PACKAGE. No DLL change. Do not touch: EVALUATION.md, README.md, docs/, etlbox.lic, Engine/Generation/, Engine/PipelineCore.cs, Engine/Intake/ (the engine is accepted), the legacy nodes' behaviour, the palette code, and NEVER any file under SampleData/TestData/intake/. Keep to about 70 tool calls; commit after each numbered step that builds and passes; if you run out, stop at the end of a finished step and say exactly where.

0. Diagnose first, in writing. Before changing anything, find out WHY nothing opens, from the code: (a) is the "Open Intake Studio" button defined anywhere that is really shown (the node template, the settings panel)? (b) is the double-click binding on the template that really draws a File intake node (Nodify may swallow the click)? (c) is the open path wrapped in a try/catch that hides an exception (XAML parse error, missing resource key, binding to a missing property) so the user sees nothing? Put the answer in reports/taskG4c-report.md under "Cause" as a short paragraph with file and line references, and name which of (a), (b), (c), or something else, was the cause. If there is an unhandled-exception.log in the app's log folder, read it and quote the relevant line (no personal paths).

1. Test harness that loads real WPF (test project only; one commit). Add a small helper in the test project that runs an action on an STA thread with a WPF Application instance (create one per test run if none exists; guard against a second Application), loads the same resource dictionaries the app loads at start (both themes, selectable per call), and can create IntakeStudioWindow with a view model, call Measure and Arrange at a given size, and render it to a PNG with RenderTargetBitmap at a chosen DPI (96 and 144) into reports/screens/. It also installs a `TraceListener` on `PresentationTraceSources.DataBindingSource` with SourceLevel Warning during a test and returns the collected binding messages. All of this is TEST-ONLY code in the test project, never in the app. Tests that use it run in one non-parallel collection with a timeout, and must not leave the Application or threads behind (shutdown cleanly).
   - Tests (each in Light AND Dark): the window constructs with a view model without an exception; after Measure and Arrange at 1100 x 720 and at the minimum size 820 x 560, the named parts exist (name them in the XAML if they are not named: Scan button, Cancel button, Browse button, Mode box, Encoding box, containers list, details panel, preview box, totals text, Save settings and Close buttons) and none has zero size; scan i01 through the view model, wait for it, and assert the containers list holds 27 items (Auto-detect) and 19 (Markers), the totals text shows "27 containers, 0 errors, 3 warnings" in Auto-detect, selecting item 12 shows a preview that starts at the container's FILE line; no binding messages at all (zero) during the whole test. If a test reveals a XAML problem, fix the XAML and say so.
   - This will probably fail first. That is the point: report what failed and what you changed.

2. Fix the opening (one commit). Whatever the cause in step 0:
   - When a File intake node is selected, the settings panel shows a button "Open Intake Studio" at the TOP of the panel (above File path), styled like the other panel buttons, in both themes.
   - Double-click on the node on the canvas opens it too. If Nodify swallows the click, use the mechanism the other nodes use for node-level input (look for how selection or Configure is raised), and say what you used.
   - Both go through the SAME command. If opening fails for any reason, show ONE plain message box ("Intake Studio could not be opened. <one short sentence>") and write the full exception to the log; never fail silently.
   - Tests: the command opens the window (use the STA helper with an injected "window factory" if the real ShowDialog would block; the test checks the factory was called with a view model that has the node's saved settings); an exception in the factory shows the message through an injected message service and does not throw; the panel button exists in the settings panel template for File intake and not for other nodes (find it in the visual tree after Measure and Arrange).

3. Node text on the canvas (one commit). A File intake node shows: its title, the subtitle, and ONE line: the file name, or "No file chosen" when the path is empty. After a scan in this session, a second line with the scan totals ("27 containers, 3 warnings"). The generic lines "Idle | rows: 0" and "Configured" must NOT appear on a File intake node (they do not apply). Other node types keep their lines as they are. Full path stays the tooltip. Tests: with the STA helper, find the node's text in the visual tree for: empty path, a path, and after setting a scan summary; and one test that a CsvFile node still shows its old lines.

4. File picker filters (one commit). For the File intake node, both the "..." button in the settings panel and the Browse button in Intake Studio offer: "All files (*.*)" first and selected, then "Text and data files" (*.txt;*.csv;*.tsv;*.json;*.jsonl;*.xml;*.yaml;*.yml;*.log;*.dat), then "CSV files (*.csv)". Other nodes keep their current filters. Put the filter strings in one place. Test the filter strings and that the File intake node uses the all-files list while the CsvFile node still starts with CSV.

5. Screens for the report. With the STA helper, render IntakeStudioWindow after a scan of i01 (Auto-detect) with the container 12 selected, in Light and Dark at 96 DPI and at 144 DPI (150 percent), at 1100 x 720 and at 820 x 560, to reports/screens/g4c-studio-<theme>-<dpi>-<size>.png (8 files at most; keep each under 400 KB). Do not open or judge them yourself beyond checking the files exist and are not blank (check that more than 5 distinct colours exist); the user and the reviewer look at them. If a window clips at the minimum size, say so in the report; do not hide it.

6. Docs. Update the Current state in CLAUDE.md (the only doc you may edit).

Build, run dotnet test with --blame-hang-timeout 60s (exact summary line, three runs in a row) and the legacy --run-tests (baseline 43 pass, 5 fail, 4 observed), using the normal commands (the app is closed). Write reports/taskG4c-report.md: Cause; what changed per step; test names and results; the list of XAML or binding problems the new tests found and how each was fixed; every rule you chose that this task does not state (mark [U]); what you could not verify; and this manual-check list, copied exactly:

MANUAL CHECKS (Light and Dark):
 1. Select a File intake node: the settings panel starts with a button "Open Intake Studio". Press it: the window opens.
 2. Double-click the node on the canvas: the window opens.
 3. The node shows "No file chosen"; after you choose a file it shows the file name; after a scan it also shows the totals line. It does not show "Idle | rows: 0" or "Configured".
 4. The "..." picker and the Browse button show All files first; i01_sample.txt can be picked.
 5. In the Studio: Browse to i01_sample.txt, Auto-detect, Scan: the progress bar moves, 27 containers, "27 containers, 0 errors, 3 warnings".
 6. Click container 12: the preview starts at the file line shown in the Lines column.
 7. Switch to Markers and Scan: 19 containers, 2 warnings.
 8. Close with a changed setting: it asks once.
 9. Visual Studio: if it stops on a FileNotFoundException at PipelineCore.cs line 155, that is a handled exception of the old Run code; press Continue (or untick "Break when this exception type is thrown").

Reply in at most 5 lines.
```

## What you do after it finishes

Send me `reports/taskG4c-report.md` and the PNG files from `reports/screens/`. I look at the screens in both themes and at 150 percent before G5.
