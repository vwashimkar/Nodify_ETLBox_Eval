# Nodify_ETLBox_Eval

A proof of concept that evaluates two .NET libraries together:

- **[Nodify](https://github.com/miroiu/nodify)** (7.3.0) for the visual node editor
- **[ETLBox](https://www.etlbox.net)** (3.11.1, with ETLBox.Csv and ETLBox.Json) for the data pipeline

You draw a small ETL pipeline as nodes in a WPF window, run it with ETLBox, and, after a successful run, generate a standalone DLL that runs the same pipeline.

This is an evaluation project, not a production tool. The findings, with the evidence behind each one, are in [`EVALUATION.md`](EVALUATION.md).

## What the pipeline does

```
CsvSource -> FilterByCountry -> ToJson -> ParseJson -> ResultJson
```

1. Reads a CSV file (`id, first_name, last_name, email, age, location, salary, joined`; `location` is `Country|City`).
2. Keeps the rows whose country matches the one you choose.
3. Shows the filtered rows as indented JSON (all columns) in the **Filtered JSON** tab.
4. Parses that JSON back and keeps only `first_name`, `last_name` and `email`, shown in the **Parsed JSON** tab.

Country matching uses the text before the first separator, trimmed, compared ignoring case. Every value is treated as text.

## Requirements

- Windows (the UI is WPF, target `net10.0-windows`)
- .NET 10 SDK (also needed at run time for DLL generation, because the generator compiles against the SDK's reference pack)
- Visual Studio with .NET 10 support, or the `dotnet` CLI

## ETLBox license

ETLBox is a commercial library with its own license; this repository does not include one and its MIT license does not cover ETLBox.

- The free edition processes at most **5,000 rows per connector**. A larger input fails with `No valid license key was found`.
- The limit counts rows read from the source, not rows written. Without a license, T048, T049 and T052 failed (sources over 5,000 rows, with only 2,999 to 4,000 output rows). The exact boundary at 5,000 and 5,001 source rows has not been tested.
- Small inputs, such as the other test files, run without a license.
- If you have a license file, put `etlbox.lic` in the `Nodify_ETLBox` project folder. The build copies it next to `ETLBox.dll` when it exists. The file is git-ignored; never commit it.

## Build and run

```
dotnet build Nodify_ETLBox/Nodify_ETLBox.csproj
Nodify_ETLBox\bin\Debug\net10.0-windows\Nodify_ETLBox.exe
```

Or open the solution in Visual Studio and press F5.

## Using the app

1. Click **Sample** to load the five-node graph, or drag node types from the list on the left and connect them.
2. Select **CsvSource** and choose a CSV file and delimiter. Select **FilterByCountry**; its **Country** list is filled from the CSV.
3. Click **Run**. Each node shows its state and row count, and problems appear in the **Issues** tab.
4. After a successful run, **Generate DLL** becomes enabled. Choose a name and a folder **outside any project**; the app refuses folders under a `.csproj` or `.sln`.

## Using the generated DLL

The DLL needs the ETLBox 3.11.1 packages and, for inputs above 5,000 rows, an ETLBox license next to `ETLBox.dll` in the app that uses it. The generated `.cs` files are written beside the DLL.

In your own project add the three packages (`ETLBox`, `ETLBox.Csv`, `ETLBox.Json`, version 3.11.1) and a reference to the DLL:

```xml
<ItemGroup>
  <Reference Include="EtlPipeline">
    <HintPath>C:\path\to\EtlPipeline.dll</HintPath>
  </Reference>
</ItemGroup>
```

```csharp
using EtlDesigner.Generated;

var result = GeneratedPipeline.Run(csvPath, "India");   // optional third argument: delimiter
Console.WriteLine(result.ErrorMessage ?? "no error");
Console.WriteLine($"read {result.Stats.RowsRead}, matched {result.Stats.RowsMatched}");
Console.WriteLine(result.ParsedJson);
```

`Run` returns an `ErrorMessage` instead of throwing for pipeline errors. An empty country returns "Country is required.". Use the assembly name you chose in `Include=`.

## Tests

`Nodify_ETLBox/SampleData/TestData` holds 52 tests (inputs plus `expected_results.csv`). Run them with the **Run all tests** button or from the command line:

```
Nodify_ETLBox.exe --run-tests                         writes SpikeResults.csv next to the exe
Nodify_ETLBox.exe --generate-dll <folder>             generates a DLL with the default settings
Nodify_ETLBox.exe --run-tests --dll <path-to-dll>     runs the same tests through a generated DLL, writes SpikeResults_dll.csv
```

The app is a GUI program, so add `| more` after the command in `cmd` to see its console output.

With a license, the results are **48 PASS, 0 FAIL, 4 OBSERVED**, and the generated DLL gives identical results to the app. The four observed cases (Windows-1252 text, ragged rows, an unterminated quote, header-name variants) record behaviour that does not match the ideal result. They are described in `EVALUATION.md`.

## Project layout

| Path | Contents |
|---|---|
| `Nodify_ETLBox/Engine/` | The pipeline (`PipelineCore.cs`) and its settings and result types |
| `Nodify_ETLBox/Engine/Generation/` | DLL generation with Roslyn |
| `Nodify_ETLBox/UI/` | View models, converters and the Nodify graph |
| `Nodify_ETLBox/Spike/` | The test runner and the command-line switches |
| `Nodify_ETLBox/SampleData/TestData/` | The 52 test inputs and expectations |
| `docs/etlbox-api-surface.md` | ETLBox public API found by reflection |
| `EVALUATION.md` | Findings, with evidence tags and open items |
| `.github/copilot-instructions.md` | Rules given to the AI coding assistant |

## Known limitations

- Only the linear five-node chain is supported, one country per run.
- The header check is hand-written and case-insensitive, while the row lookup is exact, which explains the header-variant case (T047).
- An unterminated quote makes the whole run fail with a clear error; none of the earlier valid rows is returned.
- The Dark and Light themes are swapped.
- DLL generation needs the .NET SDK reference pack on the machine.

## How it was built

The code was written with AI assistance (GitHub Copilot in agent mode, with rules in `.github/copilot-instructions.md`), and the results were checked against the raw test output. `EVALUATION.md` records where the assistant's own conclusions were wrong and corrected.

## License

The code in this repository is released under the [MIT License](LICENSE). Nodify, ETLBox, CsvHelper, Roslyn and the other packages are separate works under their own licenses; check them before redistributing anything that includes them.
