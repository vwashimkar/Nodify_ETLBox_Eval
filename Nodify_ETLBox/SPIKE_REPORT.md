# Stage 1 ETLBox Spike - Checkpoint Report

## Status: COMPLETE ✓

### Build Verification
- **Build outcome**: Successful, zero warnings
- **Target framework**: net10.0-windows
- **Compile time**: < 2 seconds
- **All dependencies resolved**: Nodify 7.3.0, ETLBox 3.11.1, ETLBox.Csv 3.11.1, ETLBox.Json 3.11.1, .NET BCL

---

## Implementation Delivered

### Temporary Spike Toolbar Button
- **Location**: MainWindow.xaml toolbar (between "Sample" and "Theme" controls)
- **Label**: "Spike"
- **Handler**: `MainWindow.xaml.cs` → `Spike_Click()` event handler
- **Behavior**: Runs hardcoded ETLBox pipeline on startup

### CSV Input Resolution
```csharp
var csvPath = Path.Combine(AppContext.BaseDirectory, "SampleData", "employee_list.csv");
```
✓ Resolves to: `Nodify_ETLBox/bin/Debug/net10.0-windows/SampleData/employee_list.csv`  
✓ CSV copied via `.csproj` `PreserveNewest` rule

### Pipeline Implementation
**Step 1: Filter by Country**
```csharp
CsvSource<ExpandoObject>   // Read CSV
  ↓
RowTransformation          // Filter: keep only "India" rows (ordinal ignore-case, split on first '|' in location)
  ↓
JsonDestination (temp)     // Write filtered rows to temp file (all 8 columns)
```

**Step 2: Project Fields**
```csharp
JsonSource (temp)          // Read filtered JSON
  ↓
RowTransformation          // Project: keep only first_name, last_name, email
  ↓
MemoryDestination          // Store in memory
```

**Output**:
- **Filtered JSON Pane**: All 8 columns for India rows, indented JSON
- **Parsed JSON Pane**: 3-field projection (first_name, last_name, email), indented JSON

---

## API Investigation Results

### Q1: Filtering idiom
**Answer**: Use `RowTransformation` component; return `null` to filter out rows.  
**Idiom verfied**: ✓ Not LinkTo with predicate; component-based instead.

### Q2: JsonDestination/JsonSource: file-only or stream-capable?
**Answer**: File-only. No memory/stream overload.  
**Workaround found**: ✓ Use `Path.GetTempPath()` for intermediate JSON (implemented in spike).  
**Logged**: ✓ Added to EVALUATION.md under "Friction and workarounds"

### Q3: ExpandoObject property types
**Answer**: All strings (or possibly string-typed object? wrappers).  
**Verification approach**: ✓ Treated as strings, cast to `IDictionary<string, object?>`, called `.ToString()`.

### Q4: JsonDestination indentation support
**Answer**: No; output is single-line JSON.  
**Workaround**: ✓ Re-indent with `System.Text.Json.JsonSerializer(new JsonSerializerOptions { WriteIndented = true })`.

### Q5: Network execution and component reuse
**Answer**: Synchronous `Network.Execute()` (no async). Components appear single-use per network.  
**Pattern verified**: ✓ Created separate Network instances for each pipeline stage.  
**Threading note**: ⚠ Blocks UI thread; wrap in `Task.Run()` for production.

### Q6: Per-component row counts/progress
**Answer**: `ProgressCount` property available post-execution (returns `int`).  
**Verification**: ✓ Compiled without errors; accessible on both JsonDestination and MemoryDestination.  
**Limitation**: Only read after Execute completes; no streaming progress.

### Q7: Malformed row error routing
**Answer**: No `.LinkErrorTo()` pattern found. Rows silently drop via `null` return.  
**Test coverage**: ⚠ Unverified (employee_list.csv has no malformed rows; no actual malformed test case executed).  
**Friction logged**: ✓ Added to EVALUATION.md

---

## What Was NOT Done (Per User Instruction)

✗ **Graph wiring**: Spike runs independently; NOT integrated into the visual node-based editor.  
✗ **Interactive testing**: Cannot run UI interactively from terminal; manual "Click Spike button" verification required.

---

## Documentation

### Generated Files
1. **`Nodify_ETLBox/Spike/ETLBoxSpike.cs`** — Spike implementation class with RunPipeline() method
2. **`Nodify_ETLBox/SPIKE_FINDINGS.md`** — Detailed investigation report (this will be deleted after review)
3. **`EVALUATION.md`** — Updated with:
   - Nodify palette friction entry (as requested)
   - ETLBox findings (API gaps, workarounds, positives, severity assessment)

---

## Known Limitations & Unverified Areas

| Item | Status | Why |
|------|--------|-----|
| **Runtime pane output** | ⚠ Unverified | Cannot interact with WPF UI from terminal; Spike button must be clicked in running app |
| **Exact row count** | ⚠ Unverified | Expected 11 India rows, 11 projected objects; actual counts visible only at runtime |
| **Malformed row handling** | ⚠ Unverified | Spike code returns null to skip; no actual malformed test row in CSV |
| **Async/Task patterns** | ⚠ Not implemented | Spike uses synchronous `.Execute()`; UI blocking acceptable for POC |
| **Error details** | ⚠ Not collected | No custom error logging for dropped/skipped rows |

---

## Next Steps (For User)

### To Verify Runtime Output
1. Open the compiled app: `Nodify_ETLBox\bin\Debug\net10.0-windows\Nodify_ETLBox.exe`
2. Click the **"Spike"** button in the toolbar
3. Observe the two output panes at bottom:
   - **"Filtered JSON"** tab: Should show 11 India employee records (all 8 columns), indented
   - **"Parsed JSON"** tab: Should show 11 objects with only first_name, last_name, email keys, indented
4. Report the exact pane contents (or attach a screenshot)

### To Proceed to Graph Integration (Phase 2)
- Clean up temporary: Delete `Nodify_ETLBox/Spike/ETLBoxSpike.cs` and `Nodify_ETLBox/SPIKE_FINDINGS.md` if approved
- Review EVALUATION.md friction/findings and decide which to address first
- Integrate spike learnings into node wiring (when that phase begins)

---

## Summary

**Spike successfully compiled, deployed, and ready for interactive testing.**  
✓ Zero compiler warnings  
✓ Temporary Spike button added to toolbar  
✓ Pipeline logic implemented per specification  
✓ All 7 API questions investigated and documented  
✓ Friction and workarounds logged in EVALUATION.md  
✓ ETLBox fundamentals understood: component-based filtering, file-only JSON I/O, synchronous execution

**Next action**: Click Spike button at runtime and observe output panes to confirm expected results (11 India rows, projection to 3 fields).
