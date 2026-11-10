# Task G6-design for Claude Code: design only (no code change): "CSV part" source for pipelines, Execute and DLL parity

POC decision by the user: ONE CSV part per pipeline is enough. No joins across parts, no branching. The end goal: the user designs a pipeline on one example file, then generates a DLL used in another WPF app on NEW files of the same instrument (different row counts); the DLL must give the same rows as Execute in the designer. This task only produces a design document. Nothing is built, nothing is committed.

## Before you start (you, not Claude Code)

1. G5c2 is committed on `g5`, `git status` clean. (Merging `g5` into master can be done any time; it is not needed for this task.)
2. Fresh `claude` session in the repo; paste the block below. Small task: about 40 tool calls, read-only.

```
Read CLAUDE.md, docs/INTAKE_PLAN.md (the parts about FileIntake, container nodes, Execute, DLL, lineage), EVALUATION.md (only what it says about the DLL and generation), reports/taskG5c2-report.md ("API for G5c2" is in reports/taskG5c1-report.md), then read the code: Engine/PipelineCore.cs, Engine/Generation/ (all), the PipelineDefinition and node model (find them), the CSV source node and its settings, how Execute, Run and Generate DLL work today, the parity tests between Execute and the generated DLL, how ETLBox's CsvSource is created and configured (delimiter, header, skip lines, encoding, quote, culture), how the legacy `FileIntake` nodes are ignored today ("File intake nodes are not run yet."), ProjectNodeSettings, and the node palette registration.

Task G6-design: write reports/taskG6-design.md (at most 4 pages, plain language, tables where they help). NO code change, NO commit, NO new package. Do not touch etlbox.lic (never read or print it). Keep to about 40 tool calls. Be concrete and base every statement on code you read (name file and type); mark anything you could not find as "not found".

The document must answer:

1. How it works today (half a page). The pipeline model (nodes, ports, edges, PipelineDefinition), the CSV source node and which settings it has, how Execute builds an ETLBox flow, how Generate DLL produces the DLL (source generation or something else; which assemblies the DLL references; does it contain settings as data), how parity between Execute and the DLL is tested today (test names).

2. The "CSV part" source. Design the smallest change that lets a pipeline start from one CSV part of a File intake node. Compare at least two options, e.g. (A) a new node type "CSV part" that references a File intake node by id and a part selector; (B) a new mode of the existing CSV source node ("From File intake"). For each: what the user does on the canvas, what is stored in the .etlproj, what changes in the model, what the output schema (column names and types) is and how it gets to downstream nodes at design time (the design-time column read must use the same part), risks. Recommend one.

3. Reading the part. ETLBox's CsvSource reads a file or a stream. The part is a byte range (or line range) of the file with its own delimiter, header, skip lines and encoding. Describe how to feed exactly the part to the same CsvSource code path in Execute and in the DLL: a bounded read-only stream over the file (offset and length, no copy of the file), handling of the BOM, of encoding, of skip lines and of the header row, and of quoted fields that contain line breaks. Say what happens for a part larger than memory (streaming, not loading). Confirm that the original file is only read, never written or locked longer than the read.

4. The part selector for NEW files (the heart of the POC). The DLL runs on files other than the example. A part saved as "lines 97 to 101" will not fit. Design a selector that finds the right CSV part in a new file by STRUCTURE, using what already exists: detector, saved layout template (IntakeTemplate, ApplyTemplate, fingerprint, column names). Specify: what is stored in the pipeline (the template JSON embedded as data? the part index in the template's layout? the expected column names and column count?), the exact runtime steps (open file, scan, apply template, pick the part, verify it), and the verification rules with plain-language error messages for each failure (file not found; no CSV part found; more than one candidate; column names differ; column count differs; template does not match, with the similarity). A failure must stop the run with a clear message and must never read a wrong part silently. State what happens when the file is exactly the example file (same SHA-256) and when it is another file. State which rule picks between several CSV parts when the layout has many (index within the template's layout, or the part whose column names equal the saved names).

5. Execute versus DLL parity. The same selector and the same reader must be used by both. Say where that shared code lives. The intake engine (Engine/Intake) uses YamlDotNet; the DLL must not need packages the user did not approve. Compare: (i) a new class library project in the solution (for example EtlDesigner.Intake) holding the no-WPF intake code, referenced by the app and by generated DLLs; (ii) generating the needed code as source into the DLL; (iii) another option you find. Recommend one, say what the DLL would reference at runtime (list the assemblies), how big the dependency is, whether YamlDotNet can stay out of the DLL (for example by keeping the YAML check out of the part selection path), and what the user must deploy next to the DLL. The generated DLL must never contain a licence key (existing rule).

6. Lineage and archive in the DLL run. What the DLL writes to the lineage log (same TextFileLineageLog? a folder setting? none for the POC), and whether the DLL run records the file name, SHA-256, template id and part used. Keep it minimal for the POC and say what you recommend.

7. What Execute, Run and Generate DLL must do with File intake nodes and the new node (replace the "File intake nodes are not run yet." behaviour), and what happens with an old project that has a File intake node and no CSV part node.

8. Parity tests. List the tests you would write: same pipeline, the example file and two other files of the same instrument (different row counts, a blank line added, a different delimiter in the part: should fail clearly), Execute versus DLL row for row; the failure cases of section 4; large part streaming; the original file unchanged. Say which sample files in SampleData/TestData/intake/ (read only) can be used and what files you would need from the user (for example two real exports from the same instrument).

9. Build plan. Split the work into prompts of about 90 tool calls each (G6a, G6b, ...), each with its goal, what it changes, what it tests and what the user can see. Keep each independently committable. Put the riskiest part first.

10. Open questions for the user: at most five, each with your recommendation, only for decisions that are really the user's.

End the document with a one-paragraph summary and the list of files you read. Reply in at most 4 lines.
```

## What you do after it finishes

Send me `reports/taskG6-design.md`. I review it against the code facts in it and then write G6a. If you have two real exports from the same instrument with different row counts, copy them into `SampleData/TestData/intake/real/` before running the prompt, and tell me which instrument they come from.
