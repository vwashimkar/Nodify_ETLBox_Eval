using System.IO;
using System.Text;
using EtlDesigner.Engine.Intake;
using Xunit;

namespace Nodify_ETLBox.Tests;

/// <summary>
/// The auto-detector against the truth files. Tier A rows must match and fail the build; Tier B
/// rows and the real files are scored and written to reports/g2-detect-report.md, never failing.
/// real/r05 and real/r06 are the hold-out and run only with INTAKE_HOLDOUT=1.
/// </summary>
public class AutoDetectTruthTests
{
    public const string HoldOutVariable = "INTAKE_HOLDOUT";
    private const string HoldOutHeading = "## Hold-out";

    public static readonly string[] DevelopmentRealFiles = ["r01_luminex_xponent.csv", "r02_moldev_softmax_pro_utf16.txt", "r03_perkinelmer_envision.csv", "r04_quantstudio.txt"];
    public static readonly string[] HoldOutRealFiles = ["r05_agilent_gen5.txt", "r06_bmg_pherastar.csv"];

    internal sealed record TruthRow(
        int Order, string Format, string Name, long FirstLine, long LastLine, string Tier,
        int? SkipLines, string? Header, string? Delimiter, string? AcceptAlso);

    internal sealed record Scored(TruthRow Truth, string Result, ContainerInfo? Detected, string Lines);

    public static IntakeResult Detect(string path, IntakeSettings? settings = null)
        => IntakeReader.Read(new FileIntakeFactory().CreateItem(path), settings ?? new IntakeSettings(), null, CancellationToken.None);

    internal static List<TruthRow> ReadTruth(string path)
    {
        var rows = IntakeTestData.ReadCsv(path);
        var header = rows[0].ToList();
        string? Column(string[] row, string name)
        {
            var i = header.IndexOf(name);
            return i >= 0 && i < row.Length && row[i].Length > 0 ? row[i] : null;
        }

        return rows.Skip(1).Select(r => new TruthRow(
            int.Parse(Column(r, "order")!), Column(r, "truth_format")!, Column(r, "truth_name") ?? string.Empty,
            long.Parse(Column(r, "first_line")!), long.Parse(Column(r, "last_line")!), Column(r, "tier")!,
            Column(r, "skip_lines") is { } skip ? int.Parse(skip) : null, Column(r, "header"),
            Column(r, "delimiter"), Column(r, "accept_also"))).ToList();
    }

    /// <summary>soap is xml with a sub-type; a sub-type counts as right when the base format is right.</summary>
    internal static bool FormatFits(TruthRow truth, ContainerInfo detected)
    {
        static string Base(string format) => format == "soap" ? "xml" : format;
        return Base(truth.Format) == Base(detected.Format) || (truth.AcceptAlso is { } also && Base(also) == Base(detected.Format));
    }

    internal static List<Scored> Score(List<TruthRow> truth, IReadOnlyList<ContainerInfo> detected)
    {
        static bool Overlaps(ContainerInfo c, long first, long last) => c.ContentFirstLine <= last && c.ContentLastLine >= first;

        var scored = new List<Scored>();
        foreach (var row in truth)
        {
            var overlapping = detected.Where(c => c.ContentFirstLine is not null && Overlaps(c, row.FirstLine, row.LastLine)).ToList();
            var exact = overlapping.FirstOrDefault(c => c.ContentFirstLine == row.FirstLine && c.ContentLastLine == row.LastLine);
            string result;
            ContainerInfo? shown;
            if (exact is not null)
            {
                result = FormatFits(row, exact) ? "Match" : "FormatOff";
                shown = exact;
            }
            else if (overlapping.Count == 0)
            {
                result = "Missed";
                shown = null;
            }
            else if (overlapping.Count > 1)
            {
                result = "Split";
                shown = overlapping[0];
            }
            else
            {
                shown = overlapping[0];
                var coversOthers = truth.Any(t => t != row && Overlaps(shown, t.FirstLine, t.LastLine));
                result = coversOthers ? "Merged" : FormatFits(row, shown) ? "LinesOff" : "FormatOff";
            }

            var lines = string.Join(" ", overlapping.Select(c => $"{c.Format}{(c.SubType is null ? "" : "/" + c.SubType)} {c.ContentFirstLine}-{c.ContentLastLine} {c.Confidence}"));
            scored.Add(new Scored(row, result, shown, lines.Length == 0 ? "-" : lines));
        }

        return scored;
    }

    public static string TruthPathOf(string file)
        => file.StartsWith('r')
            ? Path.Combine(RealIntakeFileTests.Folder, "expected", file[..3] + "_truth.csv")
            : Path.Combine(IntakeTestData.Folder, "expected", file.StartsWith("i09") ? "i09_sample_nomarkers_truth.csv" : "i10_instrument_truth.csv");

    public static string FilePathOf(string file)
        => file.StartsWith('r') ? Path.Combine(RealIntakeFileTests.Folder, file) : IntakeTestData.PathOf(file);

    // ---- Tier A -------------------------------------------------------------------------------

    public static TheoryData<string, string> I08Files()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in IntakeTestData.ReadCsv(Path.Combine(IntakeTestData.Folder, "expected", "i08_detected_formats.csv")).Skip(1))
        {
            data.Add(Path.GetFileNameWithoutExtension(row[0]), row[1]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(I08Files))]
    public void TierA_I08_IsOneContainerOfItsFormat(string name, string format)
    {
        var result = Detect(IntakeTestData.PathOf(name));
        var nonBlank = IntakeTestData.SplitLines(name).Select((l, i) => (l.Text, Number: i + 1L)).Where(l => l.Text.Trim(' ', '\t').Length > 0).ToList();

        var container = Assert.Single(result.Containers);
        Assert.Equal(format, container.Format);
        Assert.Equal((nonBlank[0].Number, nonBlank[^1].Number), (container.ContentFirstLine!.Value, container.ContentLastLine!.Value));
    }

    public static TheoryData<string, int> TierARows()
    {
        var data = new TheoryData<string, int>();
        foreach (var file in new[] { "i09_sample_nomarkers", "i10_instrument_header_table_footer" })
        {
            foreach (var row in ReadTruth(TruthPathOf(file)).Where(r => r.Tier == "A"))
            {
                data.Add(file, row.Order);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TierARows))]
    public void TierA_RowIsFound_WithItsFormatAndExactLines(string file, int order)
    {
        var truth = ReadTruth(TruthPathOf(file));
        var scored = Score(truth, Detect(FilePathOf(file)).Containers).Single(s => s.Truth.Order == order);

        Assert.True(scored.Result == "Match",
            $"{file} row {order} ({scored.Truth.Format} {scored.Truth.FirstLine}-{scored.Truth.LastLine}): {scored.Result}; detected {scored.Lines}");
    }

    // ---- Tier B report --------------------------------------------------------------------

    [Fact]
    public void TierB_AndRealFiles_AreScored_AndTheReportIsWritten()
    {
        var sections = new List<(string File, List<Scored> Rows)>
        {
            ("i09_sample_nomarkers (tier B rows)", Score(ReadTruth(TruthPathOf("i09_sample_nomarkers")), Detect(FilePathOf("i09_sample_nomarkers")).Containers)
                .Where(s => s.Truth.Tier == "B").ToList())
        };
        sections.AddRange(DevelopmentRealFiles.Select(f => (f, Score(ReadTruth(TruthPathOf(f)), Detect(FilePathOf(f)).Containers))));

        var path = ReportPath();
        var holdOut = File.Exists(path) ? ExistingHoldOut(File.ReadAllText(path)) : null;
        var text = new StringBuilder();
        text.AppendLine("# G2 auto-detect report");
        text.AppendLine();
        text.AppendLine("Written by `AutoDetectTruthTests`. Tier B rows and the real files never fail the build. Results: Match (format and exact lines), LinesOff, FormatOff, Merged, Split, Missed.");
        text.AppendLine();
        AppendSections(text, sections);
        if (holdOut is not null)
        {
            text.AppendLine(holdOut);
        }

        File.WriteAllText(path, text.ToString());
        Assert.True(File.Exists(path), $"The report could not be written to {path}.");
    }

    [HoldOutFact]
    [Trait("Category", "HoldOut")]
    public void HoldOut_R05AndR06_AreScoredOnce()
    {
        var sections = HoldOutRealFiles.Select(f => (f, Score(ReadTruth(TruthPathOf(f)), Detect(FilePathOf(f)).Containers))).ToList();
        foreach (var file in HoldOutRealFiles)
        {
            AutoDetectPropertyTests.AssertEveryNonBlankLineInExactlyOneContainer(FilePathOf(file));
        }

        var path = ReportPath();
        var existing = File.Exists(path) ? File.ReadAllText(path) : "# G2 auto-detect report\n";
        var at = existing.IndexOf(HoldOutHeading, StringComparison.Ordinal);
        var text = new StringBuilder(at >= 0 ? existing[..at] : existing.TrimEnd() + Environment.NewLine + Environment.NewLine);
        text.AppendLine(HoldOutHeading);
        text.AppendLine();
        text.AppendLine($"Run once on {DateTime.Now:yyyy-MM-dd HH:mm}, after the rules were committed. No rule was changed afterwards.");
        text.AppendLine();
        AppendSections(text, sections);

        File.WriteAllText(path, text.ToString());
    }

    private static string? ExistingHoldOut(string report)
    {
        var at = report.IndexOf(HoldOutHeading, StringComparison.Ordinal);
        return at >= 0 ? report[at..] : null;
    }

    private static void AppendSections(StringBuilder text, List<(string File, List<Scored> Rows)> sections)
    {
        string[] results = ["Match", "LinesOff", "FormatOff", "Merged", "Split", "Missed"];
        var totals = results.ToDictionary(r => r, _ => 0);
        foreach (var (file, rows) in sections)
        {
            text.AppendLine($"### {file}");
            text.AppendLine();
            text.AppendLine("| # | Truth | Lines | Detected | Result | Skip, header, delimiter |");
            text.AppendLine("|---|---|---|---|---|---|");
            foreach (var s in rows)
            {
                text.AppendLine($"| {s.Truth.Order} | {s.Truth.Format} | {s.Truth.FirstLine}-{s.Truth.LastLine} | {s.Lines} | {s.Result} | {Details(s)} |");
                totals[s.Result]++;
            }

            text.AppendLine();
            text.AppendLine(string.Join(", ", results.Select(r => $"{r} {rows.Count(s => s.Result == r)}")));
            text.AppendLine();
        }

        var matches = sections.SelectMany(s => s.Rows).Where(s => s.Result == "Match" && s.Truth.SkipLines is not null).ToList();
        text.AppendLine("### Totals");
        text.AppendLine();
        text.AppendLine(string.Join(", ", results.Select(r => $"{r} {totals[r]}")) + $" (of {totals.Values.Sum()} rows)");
        if (matches.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"Of {matches.Count} matches with skip, header and delimiter in the truth: skip lines right {matches.Count(SkipRight)}, "
                            + $"header right {matches.Count(HeaderRight)}, delimiter right {matches.Count(DelimiterRight)}.");
        }

        text.AppendLine();
    }

    private static bool SkipRight(Scored s) => s.Detected!.SkipLines == (s.Truth.SkipLines ?? 0);

    private static bool HeaderRight(Scored s)
        => s.Truth.Header switch
        {
            "yes" => s.Detected!.HeaderGuess == HeaderGuess.Yes,
            "no" => s.Detected!.HeaderGuess is HeaderGuess.No or null,
            _ => true
        };

    private static bool DelimiterRight(Scored s)
        => (s.Truth.Delimiter == "TAB" ? "\t" : s.Truth.Delimiter) is not { } delimiter || s.Detected!.Delimiter is null || s.Detected.Delimiter == delimiter;

    private static string Details(Scored s)
        => s.Result != "Match" || s.Truth.SkipLines is null
            ? string.Empty
            : $"skip {(SkipRight(s) ? "ok" : "off")}, header {(HeaderRight(s) ? "ok" : "off")}, delimiter {(DelimiterRight(s) ? "ok" : "off")}";

    /// <summary>reports/ in the repository root (git-ignored), found by walking up to the .slnx file.</summary>
    public static string ReportPath()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !folder.EnumerateFiles("*.slnx").Any())
        {
            folder = folder.Parent;
        }

        var root = folder?.FullName ?? throw new InvalidOperationException("The repository root (with the .slnx file) was not found, so the report cannot be written.");
        Directory.CreateDirectory(Path.Combine(root, "reports"));
        return Path.Combine(root, "reports", "g2-detect-report.md");
    }
}

/// <summary>Runs only when INTAKE_HOLDOUT=1, so the hold-out files are not used while the rules are tuned.</summary>
internal sealed class HoldOutFactAttribute : FactAttribute
{
    public HoldOutFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(AutoDetectTruthTests.HoldOutVariable) != "1")
        {
            Skip = $"hold-out: set {AutoDetectTruthTests.HoldOutVariable}=1 to run r05 and r06 once";
        }
    }
}

/// <summary>Every non-blank line lies in exactly one container, and the containers are in file order.</summary>
public class AutoDetectPropertyTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(IntakeTestData.Folder, "i*.txt").Order())
        {
            data.Add(file);
        }

        foreach (var file in AutoDetectTruthTests.DevelopmentRealFiles)
        {
            data.Add(Path.Combine(RealIntakeFileTests.Folder, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryNonBlankLine_IsInExactlyOneContainer(string path) => AssertEveryNonBlankLineInExactlyOneContainer(path);

    internal static void AssertEveryNonBlankLineInExactlyOneContainer(string path)
    {
        var item = new FileIntakeFactory().CreateItem(path);
        var blank = new List<bool>();
        LineIndexer.Scan(item, 500, "utf-8", (in IntakeLine line) => blank.Add(line.IsBlank), () => 0, null, CancellationToken.None);
        var result = IntakeReader.Read(item, new IntakeSettings(), null, CancellationToken.None);
        Assert.Null(result.Error);

        var owner = new int[blank.Count + 1];
        long previousLast = 0;
        foreach (var c in result.Containers)
        {
            Assert.True(c.ContentFirstLine > previousLast, $"container {c.Order} starts at {c.ContentFirstLine}, not after line {previousLast}");
            previousLast = c.ContentLastLine!.Value;
            for (var line = c.ContentFirstLine!.Value; line <= c.ContentLastLine; line++)
            {
                Assert.True(owner[line] == 0, $"line {line} is in containers {owner[line]} and {c.Order}");
                owner[line] = c.Order;
            }
        }

        for (var line = 1; line <= blank.Count; line++)
        {
            Assert.True(blank[line - 1] || owner[line] != 0, $"{Path.GetFileName(path)}: line {line} is in no container");
        }
    }
}
