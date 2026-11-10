using System.Diagnostics;
using System.Globalization;
using System.IO;
using EtlDesigner.Engine.Csv;

namespace EtlDesigner.Engine.Intake;

/// <summary>Why a CSV part was not found. <see cref="None"/> means it was found.</summary>
public enum CsvPartFailure
{
    None,
    FileMissing,
    FileLocked,
    FileUnreadable,
    Cancelled,
    NoMatch,
    PartNotPlaced,
    SeveralParts,
    NotATable,
    DelimiterDiffers,
    ColumnCountDiffers,
    ColumnNamesDiffer,
    ExampleMoved,
    Unexpected
}

/// <summary>
/// The outcome of <see cref="CsvPartLocator.LocateAsync"/>. On success <see cref="Part"/> is ready for
/// <see cref="CsvRecordReader"/>; otherwise <see cref="Reason"/> and one plain sentence in
/// <see cref="Message"/>. <see cref="FileName"/> is the name only, never the folder. The facts known
/// when it stopped are filled in (for example the similarity of a layout that did not match).
/// </summary>
public sealed record CsvPartLocation(
    bool Ok,
    CsvPartFailure Reason,
    string Message,
    CsvPartSource? Part,
    string FileName,
    string? Sha256,
    Guid TemplateId,
    string TemplateName,
    double? Similarity,
    TemplateMatch? Match,
    int? PartNumber,
    long? FirstLine,
    long? LastLine,
    IReadOnlyList<string> ColumnNames,
    long ElapsedMs);

/// <summary>
/// Finds a pipeline's CSV part in a file (G6 design section 4): scans the file with the same detector
/// and settings, applies the selector's layout template, takes the part made from the chosen template
/// part, and checks it strictly. Any doubt stops with a plain sentence; nothing is guessed. The file
/// is only read. No lineage is written. Never throws for an expected failure.
/// </summary>
public static class CsvPartLocator
{
    public const string FileMissingMessage = "The file was not found. Check the path and run again.";
    public const string FileLockedMessage = "The file is open in another program. Close it and run again.";
    public const string CancelledMessage = "The search for the CSV part was cancelled.";
    public const string ExampleMovedMessage = "The pipeline no longer finds its own example part; open Intake Studio and choose the part again.";
    public const string UnexpectedMessage = "The CSV part could not be found because of an unexpected problem; open the file in Intake Studio to check it.";

    public static async Task<CsvPartLocation> LocateAsync(string path, CsvPartSelector selector, CancellationToken token,
        IntakeSettings? settings = null, TemplateSettings? templateSettings = null, CsvPartSettings? partSettings = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(selector);
        partSettings ??= new CsvPartSettings();
        templateSettings ??= new TemplateSettings();
        var clock = Stopwatch.StartNew();
        string? sha = null;
        double? similarity = null;
        TemplateMatch? match = null;

        CsvPartLocation Result(CsvPartFailure reason, string message, CsvPartSource? part = null, ContainerInfo? c = null, IReadOnlyList<string>? names = null)
            => new(reason == CsvPartFailure.None, reason, message, part, Path.GetFileName(path), sha, selector.Template.Id, selector.Template.Name,
                similarity, match, c is null ? null : c.Order, c?.ContentFirstLine, c?.ContentLastLine, names ?? [], clock.ElapsedMilliseconds);

        try
        {
            if (token.IsCancellationRequested)
            {
                return Result(CsvPartFailure.Cancelled, CancelledMessage);
            }

            if (!File.Exists(path))
            {
                return Result(CsvPartFailure.FileMissing, FileMissingMessage);
            }

            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Result(CsvPartFailure.FileMissing, FileMissingMessage);
            }
            catch (IOException)
            {
                return Result(CsvPartFailure.FileLocked, FileLockedMessage);
            }
            catch (UnauthorizedAccessException)
            {
                return Result(CsvPartFailure.FileUnreadable, "The file cannot be read with your access rights; ask for read access and run again.");
            }

            var intakeSettings = (settings ?? new IntakeSettings()) with { Mode = selector.Mode, Encoding = selector.Encoding };
            var item = new FileIntakeFactory().CreateItem(path);
            var scan = await new IntakeScanService(intakeSettings).ScanAsync(item, null, token).ConfigureAwait(false);
            if (scan.Cancelled || token.IsCancellationRequested)
            {
                return Result(CsvPartFailure.Cancelled, CancelledMessage);
            }

            if (scan.Error is not null || scan.Intake?.File is null)
            {
                return Result(CsvPartFailure.FileUnreadable, scan.Error ?? scan.Intake?.Error ?? UnexpectedMessage);
            }

            sha = scan.Intake.File.Sha256;
            var session = new IntakeEditSession(item, scan, null, item.Id.ToString("D"), intakeSettings);
            var candidate = IntakeTemplateMatcher.Match(session.AutomaticFingerprint(templateSettings), [selector.Template], templateSettings).Single();
            similarity = candidate.Similarity;
            match = candidate.Match;
            if (candidate.Match == TemplateMatch.None)
            {
                return Result(CsvPartFailure.NoMatch,
                    $"This file does not look like the example (similarity {Number(candidate.Similarity)}; at least {Number(templateSettings.CloseThreshold)} is needed); check that it comes from the same instrument.");
            }

            var name = selector.PartName;
            var applied = session.ApplyTemplate(selector.Template, templateSettings);
            if (token.IsCancellationRequested)
            {
                return Result(CsvPartFailure.Cancelled, CancelledMessage);
            }

            if (!applied.Ok || applied.PartMap is null)
            {
                return Result(CsvPartFailure.PartNotPlaced, $"The CSV part '{name}' was not found in this file ({Clause(applied.Message)}).");
            }

            var hits = Enumerable.Range(0, applied.PartMap.Count).Where(i => applied.PartMap[i] == selector.PartIndex).ToList();
            if (hits.Count == 0)
            {
                var why = applied.Skipped.Count > 0 ? applied.Skipped[0] : "the layout left its lines as detected";
                return Result(CsvPartFailure.PartNotPlaced, $"The CSV part '{name}' was not found in this file ({why}).");
            }

            // Parts the layout left as detected but that look exactly like the expected part are copies
            // too (a file with more copies than the example): the pipeline cannot know which one to read.
            var copies = hits.Count + Enumerable.Range(0, applied.PartMap.Count)
                .Where(i => applied.PartMap[i] < 0)
                .Count(i => LooksLikeExpected(item, session.Containers[i], selector.Expected, session.EncodingName, partSettings));
            if (copies > 1)
            {
                hits = Enumerable.Repeat(0, copies).ToList();
            }

            if (hits.Count > 1)
            {
                return Result(CsvPartFailure.SeveralParts, $"This file has {hits.Count.ToString("N0", CultureInfo.InvariantCulture)} CSV parts like '{name}'; the pipeline reads one.");
            }

            var c = session.Containers[hits[0]];
            var expected = selector.Expected;
            if (c.Format is not ("csv" or "tsv") || c.ContentStartByte is null || c.ContentEndByte is null || c.ContentFirstLine is null)
            {
                return Result(CsvPartFailure.NotATable, $"Part {c.Order} of this file is not a table, but the pipeline expects a {DelimiterWord(expected.Delimiter)} table.", c: c);
            }

            // The detector's own delimiter for these lines: applying the layout sets the expected one.
            var detected = scan.Intake.Containers.FirstOrDefault(a => a.ContentFirstLine <= c.ContentFirstLine && c.ContentFirstLine <= a.ContentLastLine);
            var found = detected is { Format: "csv" or "tsv", Delimiter: { } d } && d != expected.Delimiter ? d : c.Delimiter;
            if (found != expected.Delimiter)
            {
                return Result(CsvPartFailure.DelimiterDiffers,
                    $"Part {c.Order} of this file is a {DelimiterWord(found)} table, but the pipeline expects a {DelimiterWord(expected.Delimiter)} table.", c: c);
            }

            var head = CsvPartHead.Read(item, c, session.EncodingName, partSettings);
            if (head.Error is not null)
            {
                return Result(CsvPartFailure.FileUnreadable, head.Error, c: c);
            }

            if (head.Names.Count != expected.ColumnCount)
            {
                return Result(CsvPartFailure.ColumnCountDiffers,
                    $"The CSV part '{name}' has {head.Names.Count} columns in this file, but the pipeline expects {expected.ColumnCount}.", c: c, names: head.Names);
            }

            if (expected.HasHeader && !head.Names.Select(n => n.Trim()).SequenceEqual(expected.ColumnNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase))
            {
                return Result(CsvPartFailure.ColumnNamesDiffer,
                    $"The column names of '{name}' differ: expected {List(expected.ColumnNames, partSettings)}; found {List(head.Names, partSettings)}.", c: c, names: head.Names);
            }

            if (string.Equals(sha, selector.ExampleSha256, StringComparison.OrdinalIgnoreCase)
                && (c.ContentFirstLine != selector.ExampleFirstLine || c.ContentLastLine != selector.ExampleLastLine))
            {
                return Result(CsvPartFailure.ExampleMoved, ExampleMovedMessage, c: c, names: head.Names);
            }

            var source = new CsvPartSource(path, c.ContentStartByte.Value, c.ContentEndByte.Value - c.ContentStartByte.Value, session.EncodingName,
                c.Delimiter!, c.HeaderGuess == HeaderGuess.Yes, c.SkipLines, c.ContentFirstLine.Value);
            var message = $"Found the CSV part '{name}' as part {c.Order} of this file, lines {c.ContentFirstLine.Value.ToString("N0", CultureInfo.InvariantCulture)} to {c.ContentLastLine!.Value.ToString("N0", CultureInfo.InvariantCulture)}.";
            return Result(CsvPartFailure.None, message, source, c, head.Names);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Result(CsvPartFailure.Cancelled, CancelledMessage);
        }
        catch (Exception)
        {
            return Result(CsvPartFailure.Unexpected, UnexpectedMessage);
        }
    }

    /// <summary>
    /// A csv or tsv part with the expected delimiter, header setting and columns (names compared as in
    /// the final check). A part whose delimiter or header is not known (a marker segment is not
    /// detected) is read with the expected ones.
    /// </summary>
    private static bool LooksLikeExpected(IntakeItem item, ContainerInfo c, CsvPartExpected expected, string encodingName, CsvPartSettings settings)
    {
        var headerKnown = c.HeaderGuess is HeaderGuess.Yes or HeaderGuess.No;
        if (c.Format is not ("csv" or "tsv") || (c.Delimiter is not null && c.Delimiter != expected.Delimiter)
            || (headerKnown && (c.HeaderGuess == HeaderGuess.Yes) != expected.HasHeader)
            || c.ContentStartByte is null || c.ContentEndByte is null)
        {
            return false;
        }

        var shape = c with { Delimiter = expected.Delimiter, HeaderGuess = expected.HasHeader ? HeaderGuess.Yes : HeaderGuess.No, SkipLines = expected.SkipLines };
        var head = CsvPartHead.Read(item, shape, encodingName, settings);
        return head.Error is null && head.Names.Count == expected.ColumnCount
               && head.Names.Select(n => n.Trim()).SequenceEqual(expected.ColumnNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    public static string DelimiterWord(string? delimiter) => delimiter switch
    {
        "," => "comma",
        ";" => "semicolon",
        "\t" => "tab",
        "|" => "pipe",
        " " => "space",
        null or "" => "unknown",
        _ => $"'{delimiter}'"
    };

    private static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A sentence used inside brackets: no trailing full stop.</summary>
    private static string Clause(string sentence) => sentence.TrimEnd().TrimEnd('.');

    private static string List(IReadOnlyList<string> names, CsvPartSettings settings)
    {
        var text = string.Join(", ", names);
        return text.Length <= settings.MaxNamesInMessage ? text : text[..settings.MaxNamesInMessage].TrimEnd(' ', ',') + " ...";
    }
}
