# CsvPipelineConsumer

A small console app that uses a DLL made by **Generate DLL** for the pipeline CsvFile -> ParseValue -> DotNetObject. It shows that the DLL works in a separate app.

It is not part of `Nodify_ETLBox_Eval.slnx`, so the normal build and tests do not build it.

## What it needs

- The .NET 10 SDK.
- The NuGet packages `ETLBox` 3.11.1 and `ETLBox.Csv` 3.11.1. These are the same versions as the designer app, so they are normally already in the local NuGet cache.
- A DLL generated with the default names: namespace `Generated`, class `CsvPipeline`.
- Your own ETLBox licence for files above 5,000 rows. The free edition stops after 5,000 rows read. The DLL contains no licence key, and this project copies no licence file.

## Commands (run them in the repository folder)

1. Generate the DLL. In the designer, build CsvFile -> ParseValue -> DotNetObject, press Execute and Run once, then press Generate DLL. Keep the names `CsvPipeline` and `Generated`, and choose a folder outside the repository, for example `C:\Users\<you>\Documents\CsvPipeline`.

2. Run the consumer on a CSV file and pass the DLL path:

   ```
   dotnet run --project Consumer/CsvPipelineConsumer -p:PipelineDll="C:\Users\<you>\Documents\CsvPipeline\CsvPipeline.dll" -- Nodify_ETLBox/SampleData/TestData/composite/c01_basic.csv
   ```

   Without `-p:PipelineDll=...`, the project looks for `Consumer/out/CsvPipeline.dll`. That folder is git-ignored.

3. After you generate a new DLL, build the consumer again so it copies the new DLL. `dotnet run` (without `--no-build`) does this.

The app prints the progress, the counts, the first 5 rows and the rejects. Ctrl+C cancels the run.
