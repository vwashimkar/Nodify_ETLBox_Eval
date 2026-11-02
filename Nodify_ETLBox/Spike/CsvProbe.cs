using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ETLBox;
using ETLBox.Csv;
using ETLBox.DataFlow;

namespace EtlDesigner.Spike;

public record CsvProbeRow(string Id, string Question, string Result, string Evidence, string HowObtained);

public static class CsvProbe
{
    public static async Task<List<CsvProbeRow>> RunAsync(string sampleDataDir)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var rows = new List<CsvProbeRow>();
        var testDataDir = Path.Combine(sampleDataDir, "TestData");
        var compositeDir = Path.Combine(testDataDir, "composite");
        var baseline = Path.Combine(testDataDir, "01_baseline_clean.csv");
        var c10 = Path.Combine(compositeDir, "c10_large.csv");
        var c07 = Path.Combine(compositeDir, "c07_no_header.csv");
        var c14 = Path.Combine(compositeDir, "c14_preamble.csv");
        var c05 = Path.Combine(compositeDir, "c05_quoting.csv");
        var c06 = Path.Combine(compositeDir, "c06_semicolon.csv");
        var cp1252File = Path.Combine(testDataDir, "06_encoding_windows1252.csv");
        var unterminated = Path.Combine(testDataDir, "12b_unterminated_quote.csv");

        rows.Add(await ProbeP1BasicAsync(baseline));
        rows.Add(await ProbeP1LargeAsync(c10));
        rows.Add(await ProbeP2NoHeaderAsync(c07));
        rows.Add(await ProbeP3EncodingAsync(cp1252File));
        rows.Add(await ProbeP4SkipLinesAsync(c14));
        rows.Add(await ProbeP4bSkipLinesWithDelegateAsync(c14));
        rows.Add(await ProbeP5QuoteCharAsync(sampleDataDir));
        rows.Add(await ProbeP6LineNumbersAsync(c05));
        rows.Add(await ProbeP6bErrorRowInfoAsync(unterminated, c05));
        rows.Add(await ProbeP7ProgressCancelAsync(c10));
        rows.Add(await ProbeP7bProgressCostAsync(c10));
        rows.Add(await ProbeP8DelimiterAndQuotingAsync(c06, c05));

        return rows;
    }

    public static void WriteMarkdown(string outputPath, IReadOnlyList<CsvProbeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CSV probe results");
        sb.AppendLine();
        sb.AppendLine("| id | question | result | evidence | how the answer was obtained |");
        sb.AppendLine("|---|---|---|---|---|");

        foreach (var row in rows)
        {
            sb.Append("| ").Append(EscapeCell(row.Id)).Append(" | ")
              .Append(EscapeCell(row.Question)).Append(" | ")
              .Append(EscapeCell(row.Result)).Append(" | ")
              .Append(EscapeCell(row.Evidence)).Append(" | ")
              .Append(EscapeCell(row.HowObtained)).AppendLine(" |");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static async Task<CsvProbeRow> ProbeP1BasicAsync(string filePath)
    {
        const string question = "P1a Row-limited read on 01_baseline_clean.csv (60 rows), limit 5, using ExecuteAsync(CancellationToken) and Cancel(); then exclusive open check.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P1a", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        var tokenRun = await RunLimitedReadAsync(filePath, 5, useCancellationToken: true, useNetworkCancel: false);
        var tokenOpen = CanOpenExclusively(filePath, out var tokenOpenMessage);

        var cancelRun = await RunLimitedReadAsync(filePath, 5, useCancellationToken: false, useNetworkCancel: true);
        var cancelOpen = CanOpenExclusively(filePath, out var cancelOpenMessage);

        var result = IsUnexpected(tokenRun) || IsUnexpected(cancelRun) ? "unexpected error" : "observed";
        var evidence = $"token rows={tokenRun.RowsSeen}, elapsedMs={tokenRun.ElapsedMs}, exceptionType={tokenRun.ExceptionType}; reopen={(tokenOpen ? "ok" : "failed")} ({tokenOpenMessage}); " +
                       $"cancel rows={cancelRun.RowsSeen}, elapsedMs={cancelRun.ElapsedMs}, exceptionType={cancelRun.ExceptionType}; reopen={(cancelOpen ? "ok" : "failed")} ({cancelOpenMessage})";

        return new CsvProbeRow("P1a", question, result, evidence, "CsvSource<ExpandoObject> read with row-limit cancellation (token and Network.Cancel) and immediate exclusive file-open checks.");
    }

    private static async Task<CsvProbeRow> ProbeP1LargeAsync(string filePath)
    {
        const string question = "P1b Row-limited read on composite/c10_large.csv (limit 5) using token and Cancel(); reopen immediately, then repeat each cancel+reopen cycle 10 times.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P1b", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        var tokenFirst = await RunLimitedReadAsync(filePath, 5, useCancellationToken: true, useNetworkCancel: false);
        var tokenFirstOpen = CanOpenExclusively(filePath, out var tokenFirstOpenMessage);
        var tokenCycle = await RepeatCancelAndReopenAsync(filePath, useCancellationToken: true, useNetworkCancel: false, iterations: 10);

        var cancelFirst = await RunLimitedReadAsync(filePath, 5, useCancellationToken: false, useNetworkCancel: true);
        var cancelFirstOpen = CanOpenExclusively(filePath, out var cancelFirstOpenMessage);
        var cancelCycle = await RepeatCancelAndReopenAsync(filePath, useCancellationToken: false, useNetworkCancel: true, iterations: 10);

        var hasUnexpected = IsUnexpected(tokenFirst) || IsUnexpected(cancelFirst)
            || tokenCycle.AnyUnexpected
            || cancelCycle.AnyUnexpected
            || !tokenFirstOpen
            || !cancelFirstOpen;
        var result = hasUnexpected ? "unexpected error" : "observed";

        var evidence = $"token first rows={tokenFirst.RowsSeen}, elapsedMs={tokenFirst.ElapsedMs}, exceptionType={tokenFirst.ExceptionType}, reopen={(tokenFirstOpen ? "ok" : "failed")} ({tokenFirstOpenMessage}), cycleFailures={tokenCycle.FailureCount}; " +
                       $"cancel first rows={cancelFirst.RowsSeen}, elapsedMs={cancelFirst.ElapsedMs}, exceptionType={cancelFirst.ExceptionType}, reopen={(cancelFirstOpen ? "ok" : "failed")} ({cancelFirstOpenMessage}), cycleFailures={cancelCycle.FailureCount}; " +
                       $"tokenCycleNote={tokenCycle.Note}; cancelCycleNote={cancelCycle.Note}";

        return new CsvProbeRow("P1b", question, result, evidence, "Ran row-limited reads (limit=5) for token cancellation and Network.Cancel, checked exclusive reopen each time, then repeated each mode for 10 run+reopen cycles.");
    }

    private static async Task<CsvProbeRow> ProbeP2NoHeaderAsync(string filePath)
    {
        const string question = "P2 No header mode on c07_no_header.csv: exact generated column/property names and setting used.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P2", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        try
        {
            var data = await ReadCsvAsync(filePath, csv =>
            {
                csv.Configuration.HasHeaderRecord = false;
            });

            var first = data.FirstOrDefault() as IDictionary<string, object?>;
            if (first == null)
            {
                return new CsvProbeRow("P2", question, "unexpected error", "No rows returned.", "Set Configuration.HasHeaderRecord=false and inspected ExpandoObject keys.");
            }

            var keys = string.Join(", ", first.Keys);
            var evidence = $"Configuration.HasHeaderRecord=false; first row property names: {keys}";
            return new CsvProbeRow("P2", question, "observed", evidence, "Set CsvSource.Configuration.HasHeaderRecord=false and collected first row keys from ExpandoObject.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P2", question, "unexpected error", ex.ToString(), "Set Configuration.HasHeaderRecord=false and attempted read.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP3EncodingAsync(string filePath)
    {
        const string question = "P3 Encoding check on 06_encoding_windows1252.csv: default vs Encoding=Windows-1252, value for row containing Mexico.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P3", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        try
        {
            var defaultRows = await ReadCsvAsync(filePath, null);
            var cpRows = await ReadCsvAsync(filePath, csv =>
            {
                csv.Encoding = Encoding.GetEncoding(1252);
            });

            var defaultValue = FindRowWithMexico(defaultRows);
            var cpValue = FindRowWithMexico(cpRows);

            var evidence = $"default-read='{defaultValue}'; windows-1252-read='{cpValue}'";
            return new CsvProbeRow("P3", question, "observed", evidence, "Read twice with CsvSource<ExpandoObject>, once default encoding and once CsvSource.Encoding=Encoding.GetEncoding(1252). Compared row containing Mexico.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P3", question, "unexpected error", ex.ToString(), "Ran default and windows-1252 reads and compared values.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP4SkipLinesAsync(string filePath)
    {
        const string question = "P4 Skip lines in c14_preamble.csv (3 lines before header): is there a usable ETLBox/Csv configuration property?";
        if (!File.Exists(filePath))
        {
            return NotPossible("P4", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        try
        {
            var csv = new CsvSource<ExpandoObject>(filePath);
            var configType = csv.Configuration.GetType();
            var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n.Contains("Skip", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Header", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Preamble", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .ToList();

            var hasSkipLines = properties.Any(p => string.Equals(p, "SkipLines", StringComparison.OrdinalIgnoreCase));
            var evidence = $"Relevant CsvConfiguration properties: {string.Join(", ", properties)}";

            if (hasSkipLines)
            {
                return new CsvProbeRow("P4", question, "observed", evidence + "; property used: SkipLines", "Inspected CsvSource.Configuration public properties via reflection.");
            }

            return new CsvProbeRow("P4", question, "not possible", evidence + "; no SkipLines property found.", "Inspected CsvSource.Configuration public properties via reflection; no direct skip-lines property was available to set in this probe.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P4", question, "unexpected error", ex.ToString(), "Reflected over CsvSource.Configuration members.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP4bSkipLinesWithDelegateAsync(string filePath)
    {
        const string question = "P4b Skip lines on composite/c14_preamble.csv using Configuration.ShouldSkipRecord for first 3 records, with and without header.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P4b", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        try
        {
            var withHeader = await ReadWithSkipDelegateAsync(filePath, hasHeader: true);
            var withoutHeader = await ReadWithSkipDelegateAsync(filePath, hasHeader: false);

            var result = (withHeader.ExceptionText == null && withoutHeader.ExceptionText == null) ? "observed" : "unexpected error";
            var evidence = $"delegateType={withHeader.DelegateType}; withHeader columns={withHeader.ColumnNames}; firstRow={withHeader.FirstRow}; rowNumbersSeen={withHeader.RowNumbers}; exception={withHeader.ExceptionText ?? "<none>"}; " +
                           $"headerOff columns={withoutHeader.ColumnNames}; firstRow={withoutHeader.FirstRow}; rowNumbersSeen={withoutHeader.RowNumbers}; exception={withoutHeader.ExceptionText ?? "<none>"}; numberingBasis={withHeader.NumberingBasis}";

            return new CsvProbeRow("P4b", question, result, evidence, "Set CsvSource.Configuration.ShouldSkipRecord delegate (args => args.Row.Parser.Row <= 3), read with header on and off, and inspected resulting columns/first row and parser row numbers.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P4b", question, "unexpected error", ex.ToString(), "Attempted skip-lines probe using Configuration.ShouldSkipRecord.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP5QuoteCharAsync(string sampleDataDir)
    {
        const string question = "P5 Quote character support with single quote (') and embedded delimiter value.";
        var tempFile = Path.Combine(sampleDataDir, "TestData", "composite", "probe_quote_single.csv");

        try
        {
            File.WriteAllText(tempFile, "id;value\n1;'left;right'\n", Encoding.UTF8);

            var rows = await ReadCsvAsync(tempFile, csv =>
            {
                csv.Configuration.Delimiter = ";";
                csv.Configuration.Quote = '\'';
            });

            var first = rows.FirstOrDefault() as IDictionary<string, object?>;
            var value = first?.Values.Skip(1).FirstOrDefault()?.ToString() ?? "<missing>";
            var intact = string.Equals(value, "left;right", StringComparison.Ordinal);
            var evidence = $"read value='{value}'";

            return new CsvProbeRow("P5", question, intact ? "observed" : "unexpected error", evidence, "Created temporary CSV with quote=' and delimiter=';'. Read with CsvSource.Configuration.Quote and Delimiter.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P5", question, "unexpected error", ex.ToString(), "Created temporary file and attempted read with custom quote character.");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<CsvProbeRow> ProbeP6LineNumbersAsync(string filePath)
    {
        const string question = "P6 Line/record numbers for c05_quoting.csv (record 3 spans two lines): can ETLBox expose them per row?";
        if (!File.Exists(filePath))
        {
            return NotPossible("P6", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        try
        {
            string parserInfo = "<no context callbacks>";
            var callbackCount = 0;

            await ReadCsvAsync(filePath, csv =>
            {
                csv.CsvContext = context =>
                {
                    callbackCount++;
                    var parser = context.GetType().GetProperty("Parser")?.GetValue(context);
                    var row = parser?.GetType().GetProperty("Row")?.GetValue(parser)?.ToString();
                    var rawRow = parser?.GetType().GetProperty("RawRow")?.GetValue(parser)?.ToString();
                    if (row != null || rawRow != null)
                    {
                        parserInfo = $"Parser.Row={row ?? "<null>"}, Parser.RawRow={rawRow ?? "<null>"}";
                    }
                };
            });

            if (callbackCount == 0)
            {
                return new CsvProbeRow("P6", question, "not possible", "CsvContext callback was not invoked during read.", "Assigned CsvSource.CsvContext callback and attempted to inspect parser row fields.");
            }

            return new CsvProbeRow("P6", question, "observed", $"CsvContext callbacks={callbackCount}; {parserInfo}", "Assigned CsvSource.CsvContext callback and reflected Parser.Row/RawRow values.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P6", question, "unexpected error", ex.ToString(), "Assigned CsvSource.CsvContext callback and attempted to inspect parser line/row metadata.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP7ProgressCancelAsync(string filePath)
    {
        const string question = "P7 OnProgress + Cancel() on c10_large.csv: progress frequency and clean stop after 1,000 rows.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P7", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        var progressValues = new List<long>();
        var stopwatch = Stopwatch.StartNew();
        var rowsSeen = 0;
        string? exceptionText = null;

        try
        {
            var csv = new CsvSource<ExpandoObject>(filePath);
            var destination = new MemoryDestination<ExpandoObject>();
            var network = new Network(csv);
            csv.LinkTo(destination);

            csv.OnProgress = count =>
            {
                progressValues.Add(count);
                rowsSeen = Math.Max(rowsSeen, (int)count);
                if (count >= 1000)
                {
                    network.Cancel();
                }
            };

            await network.ExecuteAsync();
            rowsSeen = Math.Max(rowsSeen, destination.Data.Count);
        }
        catch (Exception ex)
        {
            exceptionText = ex.ToString();
        }

        stopwatch.Stop();

        var result = (exceptionText == null || IsExpectedCancellation(exceptionText)) ? "observed" : "unexpected error";
        var progressSummary = progressValues.Count == 0
            ? "<none>"
            : $"count={progressValues.Count}, first={progressValues.First()}, last={progressValues.Last()}";
        var evidence = $"rowsSeen={rowsSeen}, elapsedMs={stopwatch.ElapsedMilliseconds}, progress={progressSummary}, exception={exceptionText ?? "<none>"}";

        return new CsvProbeRow("P7", question, result, evidence, "Read c10_large.csv with CsvSource.OnProgress callback and called Network.Cancel() at progress >= 1000.");
    }

    private static async Task<CsvProbeRow> ProbeP6bErrorRowInfoAsync(string badQuoteFile, string quotingFile)
    {
        const string question = "P6b Row information on errors using LinkErrorTo + exception capture, and RowTransformation counter on good rows for c05_quoting.csv.";
        if (!File.Exists(badQuoteFile) || !File.Exists(quotingFile))
        {
            return NotPossible("P6b", question, $"Missing file(s): badQuoteExists={File.Exists(badQuoteFile)}, quotingExists={File.Exists(quotingFile)}", "Checked required file paths before probe run.");
        }

        try
        {
            var badQuote = await CaptureErrorsAsync(badQuoteFile);
            var quoting = await CaptureErrorsAsync(quotingFile);
            var transformProbe = await ProbeGoodRowCounterAsync(quotingFile);

            var reliability = badQuote.ErrorFields.Contains("Record", StringComparison.OrdinalIgnoreCase)
                || badQuote.ExceptionFacts.Contains("Row", StringComparison.OrdinalIgnoreCase)
                || badQuote.ExceptionFacts.Contains("Line", StringComparison.OrdinalIgnoreCase)
                ? "row/record details present"
                : "no reliable row index fields found";

            var evidence = $"12b errors={badQuote.ErrorCount}, errorFields={badQuote.ErrorFields}, exception={badQuote.ExceptionType}, exceptionFacts={badQuote.ExceptionFacts}; " +
                           $"c05 errors={quoting.ErrorCount}, errorFields={quoting.ErrorFields}, exception={quoting.ExceptionType}, exceptionFacts={quoting.ExceptionFacts}; " +
                           $"rowCounter={transformProbe.CounterSummary}; fileLineCount={transformProbe.FileLineCount}; reliable={reliability}";

            var result = badQuote.UnexpectedException || quoting.UnexpectedException || transformProbe.ExceptionText != null
                ? "unexpected error"
                : "observed";

            return new CsvProbeRow("P6b", question, result, evidence, "Used CsvSource<ExpandoObject>.LinkErrorTo(MemoryDestination<ETLBoxError>) plus exception capture, then used RowTransformation counter for good rows and compared with physical file lines for c05_quoting.csv.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P6b", question, "unexpected error", ex.ToString(), "Ran LinkErrorTo and row-counter probes for row/record metadata.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP7bProgressCostAsync(string filePath)
    {
        const string question = "P7b Progress callback behavior and callback cost for 1,000-row read on c10_large.csv, with and without OnProgress.";
        if (!File.Exists(filePath))
        {
            return NotPossible("P7b", question, $"File not found: {filePath}", "Checked file path before probe run.");
        }

        try
        {
            var withoutProgress = await RunThousandRowTimingAsync(filePath, withProgress: false);
            var withProgress = await RunThousandRowTimingAsync(filePath, withProgress: true);

            var callbackPattern = AnalyzeProgressPattern(withProgress.ProgressValues);
            var callbackCost = withProgress.ElapsedMs - withoutProgress.ElapsedMs;

            var result = (withoutProgress.UnexpectedException || withProgress.UnexpectedException) ? "unexpected error" : "observed";
            var evidence = $"withoutProgress elapsedMs={withoutProgress.ElapsedMs}, rows={withoutProgress.RowsSeen}, exceptionType={withoutProgress.ExceptionType}; " +
                           $"withProgress elapsedMs={withProgress.ElapsedMs}, rows={withProgress.RowsSeen}, exceptionType={withProgress.ExceptionType}, callback={callbackPattern}; callbackCostMs={callbackCost}";

            return new CsvProbeRow("P7b", question, result, evidence, "Ran 1,000-row cancellation-limited reads on c10_large.csv with and without CsvSource.OnProgress; compared callback values and elapsed times.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P7b", question, "unexpected error", ex.ToString(), "Measured OnProgress callback pattern and timing overhead.");
        }
    }

    private static async Task<CsvProbeRow> ProbeP8DelimiterAndQuotingAsync(string semicolonFile, string quotingFile)
    {
        const string question = "P8 Delimiter and quoting: semicolon file with Delimiter=';' and quoted values in c05_quoting.csv remain intact.";
        if (!File.Exists(semicolonFile) || !File.Exists(quotingFile))
        {
            return NotPossible("P8", question, $"Missing file(s): semicolonExists={File.Exists(semicolonFile)}, quotingExists={File.Exists(quotingFile)}", "Checked required file paths before probe run.");
        }

        try
        {
            var semicolonRows = await ReadCsvAsync(semicolonFile, csv =>
            {
                csv.Configuration.Delimiter = ";";
            });

            var quotingRows = await ReadCsvAsync(quotingFile, null);
            var hasRaoAsha = AnyCellContains(quotingRows, "Rao, Asha");
            var hasTwoPointFive = AnyCellContains(quotingRows, "2,5");

            var evidence = $"semicolonRows={semicolonRows.Count}; c05 contains 'Rao, Asha'={hasRaoAsha}; c05 contains '2,5'={hasTwoPointFive}";
            var result = hasRaoAsha && hasTwoPointFive ? "observed" : "unexpected error";
            return new CsvProbeRow("P8", question, result, evidence, "Read c06_semicolon.csv with Configuration.Delimiter=';'; read c05_quoting.csv with default quoting and checked cell values.");
        }
        catch (Exception ex)
        {
            return new CsvProbeRow("P8", question, "unexpected error", ex.ToString(), "Read semicolon and quoting files using CsvSource<ExpandoObject>.");
        }
    }

    private static CsvProbeRow NotPossible(string id, string question, string reason, string how)
        => new(id, question, "not possible", reason, how);

    private static bool IsExpectedCancellation(string exceptionText)
        => exceptionText.Contains(nameof(OperationCanceledException), StringComparison.Ordinal)
        || exceptionText.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnexpected(LimitedReadResult result)
    {
        if (string.Equals(result.ExceptionType, "<none>", StringComparison.Ordinal))
        {
            return false;
        }

        return result.ExpectedCancellation == false;
    }

    private static async Task<SkipDelegateResult> ReadWithSkipDelegateAsync(string filePath, bool hasHeader)
    {
        var rowNumbers = new List<long>();

        try
        {
            var rows = await ReadCsvAsync(filePath, csv =>
            {
                csv.Configuration.HasHeaderRecord = hasHeader;
                csv.Configuration.ShouldSkipRecord = args =>
                {
                    var parser = args.Row.Parser;
                    var row = Convert.ToInt64(parser.Row);
                    rowNumbers.Add(row);
                    return row <= 3;
                };
            });

            var first = rows.FirstOrDefault() as IDictionary<string, object?>;
            var columnNames = first == null ? "<none>" : string.Join(", ", first.Keys);
            var firstRow = first == null ? "<none>" : string.Join(", ", first.Select(kv => $"{kv.Key}={kv.Value}"));
            var numberingBasis = rowNumbers.Any(r => r == 0) ? "zero-based" : "one-based";
            var delegateType = typeof(CsvSource<ExpandoObject>).GetProperty("Configuration")?.PropertyType.GetProperty("ShouldSkipRecord")?.PropertyType.FullName ?? "<unknown>";

            return new SkipDelegateResult(columnNames, firstRow, string.Join(",", rowNumbers.Take(8)), numberingBasis, delegateType, null);
        }
        catch (Exception ex)
        {
            var delegateType = typeof(CsvSource<ExpandoObject>).GetProperty("Configuration")?.PropertyType.GetProperty("ShouldSkipRecord")?.PropertyType.FullName ?? "<unknown>";
            return new SkipDelegateResult("<none>", "<none>", string.Join(",", rowNumbers.Take(8)), rowNumbers.Any(r => r == 0) ? "zero-based" : "unknown", delegateType, ex.ToString());
        }
    }

    private static async Task<ErrorCaptureResult> CaptureErrorsAsync(string filePath)
    {
        var errorDestination = new MemoryDestination<ETLBoxError>();
        string? exceptionText = null;
        string exceptionType = "<none>";

        try
        {
            var source = new CsvSource<ExpandoObject>(filePath);
            var good = new MemoryDestination<ExpandoObject>();
            source.LinkTo(good);
            source.LinkErrorTo(errorDestination);
            var network = new Network(source);
            await network.ExecuteAsync();
        }
        catch (Exception ex)
        {
            exceptionText = ex.ToString();
            exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
        }

        var firstError = errorDestination.Data.FirstOrDefault();
        var errorFields = firstError == null ? "<none>" : DescribeObject(firstError);
        var exceptionFacts = exceptionText == null ? "<none>" : ExtractExceptionFacts(exceptionText);
        var unexpected = exceptionText != null && !IsExpectedCancellation(exceptionText);

        return new ErrorCaptureResult(errorDestination.Data.Count, errorFields, exceptionType, exceptionFacts, unexpected);
    }

    private static async Task<RowCounterProbeResult> ProbeGoodRowCounterAsync(string filePath)
    {
        int rowCounter = 0;
        string? exceptionText = null;

        try
        {
            var source = new CsvSource<ExpandoObject>(filePath);
            var counter = new RowTransformation(row =>
            {
                rowCounter++;
                return row;
            });
            var destination = new MemoryDestination<ExpandoObject>();
            source.LinkTo(counter);
            counter.LinkTo(destination);
            var network = new Network(source);
            await network.ExecuteAsync();
        }
        catch (Exception ex)
        {
            exceptionText = ex.ToString();
        }

        var lineCount = File.ReadAllLines(filePath).Length;
        return new RowCounterProbeResult($"goodRowsCounted={rowCounter}", lineCount, exceptionText);
    }

    private static async Task<RepeatedCycleResult> RepeatCancelAndReopenAsync(string filePath, bool useCancellationToken, bool useNetworkCancel, int iterations)
    {
        var failures = new List<string>();
        var unexpected = false;

        for (int i = 1; i <= iterations; i++)
        {
            var run = await RunLimitedReadAsync(filePath, 5, useCancellationToken, useNetworkCancel);
            if (IsUnexpected(run))
            {
                unexpected = true;
                failures.Add($"run-{i}:{run.ExceptionType}");
            }

            var reopened = CanOpenExclusively(filePath, out var message);
            if (!reopened)
            {
                failures.Add($"open-{i}:{message}");
            }
        }

        return new RepeatedCycleResult(failures.Count, unexpected, failures.Count == 0 ? "all 10 passed" : string.Join("; ", failures));
    }

    private static async Task<ProgressRunResult> RunThousandRowTimingAsync(string filePath, bool withProgress)
    {
        var cts = new CancellationTokenSource();
        var rowsSeen = 0;
        string exceptionType = "<none>";
        string? exceptionText = null;
        var progressValues = new List<long>();
        var sw = Stopwatch.StartNew();

        try
        {
            var source = new CsvSource<ExpandoObject>(filePath);
            if (withProgress)
            {
                source.OnProgress = count =>
                {
                    progressValues.Add(count);
                };
            }

            var counter = new RowTransformation(row =>
            {
                rowsSeen++;
                if (rowsSeen >= 1000)
                {
                    cts.Cancel();
                }
                return row;
            });

            var destination = new MemoryDestination<ExpandoObject>();
            source.LinkTo(counter);
            counter.LinkTo(destination);
            var network = new Network(source);
            await network.ExecuteAsync(cts.Token);
        }
        catch (Exception ex)
        {
            exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
            exceptionText = ex.ToString();
        }
        finally
        {
            sw.Stop();
        }

        var unexpected = exceptionText != null && !IsExpectedCancellation(exceptionText);
        return new ProgressRunResult(rowsSeen, sw.ElapsedMilliseconds, progressValues, exceptionType, unexpected);
    }

    private static string AnalyzeProgressPattern(IReadOnlyList<long> values)
    {
        if (values.Count == 0)
        {
            return "no callbacks";
        }

        var first = values.First();
        var last = values.Last();
        var everyRow = first == 1 && last == values.Count && values.Zip(values.Skip(1), (a, b) => b - a).All(d => d == 1);
        return everyRow
            ? $"every-row (callbacks={values.Count}, first={first}, last={last})"
            : $"interval/other (callbacks={values.Count}, first={first}, last={last})";
    }

    private static string DescribeObject(object instance)
    {
        var type = instance.GetType();
        var parts = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value;
            try
            {
                value = property.GetValue(instance);
            }
            catch
            {
                value = "<unreadable>";
            }

            parts.Add($"{property.Name}={value}");
        }

        return parts.Count == 0 ? "<no public properties>" : string.Join(", ", parts);
    }

    private static string ExtractExceptionFacts(string exceptionText)
    {
        var lines = exceptionText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var matched = lines.Where(l => l.Contains("row", StringComparison.OrdinalIgnoreCase)
                                    || l.Contains("line", StringComparison.OrdinalIgnoreCase)
                                    || l.Contains("record", StringComparison.OrdinalIgnoreCase)
                                    || l.Contains("position", StringComparison.OrdinalIgnoreCase))
                           .Take(8)
                           .ToList();
        return matched.Count == 0 ? "<none>" : string.Join(" || ", matched);
    }

    private static async Task<List<ExpandoObject>> ReadCsvAsync(string filePath, Action<CsvSource<ExpandoObject>>? configure)
    {
        var csv = new CsvSource<ExpandoObject>(filePath);
        configure?.Invoke(csv);

        var destination = new MemoryDestination<ExpandoObject>();
        csv.LinkTo(destination);

        var network = new Network(csv);
        await network.ExecuteAsync();

        return destination.Data.ToList();
    }

    private static async Task<LimitedReadResult> RunLimitedReadAsync(string filePath, int limit, bool useCancellationToken, bool useNetworkCancel)
    {
        var stopwatch = Stopwatch.StartNew();
        var rowsSeen = 0;
        string? exceptionText = null;
        var exceptionType = "<none>";
        var cancellationRequestedByProbe = false;

        var cts = new CancellationTokenSource();

        try
        {
            var csv = new CsvSource<ExpandoObject>(filePath);
            var destination = new MemoryDestination<ExpandoObject>();
            csv.LinkTo(destination);
            var network = new Network(csv);

            csv.OnProgress = count =>
            {
                rowsSeen = Math.Max(rowsSeen, (int)count);
                if (count >= limit)
                {
                    cancellationRequestedByProbe = true;
                    if (useNetworkCancel)
                    {
                        network.Cancel();
                    }

                    if (useCancellationToken)
                    {
                        cts.Cancel();
                    }
                }
            };

            if (useCancellationToken)
            {
                await network.ExecuteAsync(cts.Token);
            }
            else
            {
                await network.ExecuteAsync();
            }

            rowsSeen = Math.Max(rowsSeen, destination.Data.Count);
        }
        catch (Exception ex)
        {
            exceptionText = ex.ToString();
            exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
        }

        stopwatch.Stop();
        var expectedCancellation = cancellationRequestedByProbe && (exceptionText == null || IsExpectedCancellation(exceptionText));
        return new LimitedReadResult(rowsSeen, stopwatch.ElapsedMilliseconds, exceptionText, exceptionType, expectedCancellation);
    }

    private static string FindRowWithMexico(IEnumerable<ExpandoObject> rows)
    {
        foreach (var row in rows)
        {
            if (row is not IDictionary<string, object?> map)
            {
                continue;
            }

            foreach (var kv in map)
            {
                var value = kv.Value?.ToString() ?? string.Empty;
                if (value.Contains("Mexico", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("México", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("MÃ©xico", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{kv.Key}={value}";
                }
            }
        }

        return "<row containing Mexico not found>";
    }

    private static bool AnyCellContains(IEnumerable<ExpandoObject> rows, string needle)
    {
        return rows
            .Select(r => r as IDictionary<string, object?>)
            .Where(r => r != null)
            .SelectMany(r => r!.Values)
            .Select(v => v?.ToString() ?? string.Empty)
            .Any(v => string.Equals(v, needle, StringComparison.Ordinal));
    }

    private static bool CanOpenExclusively(string filePath, out string message)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            message = "exclusive open succeeded";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static string EscapeCell(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    private sealed record LimitedReadResult(int RowsSeen, long ElapsedMs, string? ExceptionText, string ExceptionType, bool ExpectedCancellation);
    private sealed record SkipDelegateResult(string ColumnNames, string FirstRow, string RowNumbers, string NumberingBasis, string DelegateType, string? ExceptionText);
    private sealed record ErrorCaptureResult(int ErrorCount, string ErrorFields, string ExceptionType, string ExceptionFacts, bool UnexpectedException);
    private sealed record RowCounterProbeResult(string CounterSummary, int FileLineCount, string? ExceptionText);
    private sealed record RepeatedCycleResult(int FailureCount, bool AnyUnexpected, string Note);
    private sealed record ProgressRunResult(int RowsSeen, long ElapsedMs, IReadOnlyList<long> ProgressValues, string ExceptionType, bool UnexpectedException);
}
