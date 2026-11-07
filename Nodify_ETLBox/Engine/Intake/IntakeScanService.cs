using System.Diagnostics;
using System.Globalization;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Where a scan is. <see cref="Percent"/> covers the whole scan: reading and finding containers take
/// 0 to 50, checking the containers 50 to 100 [U: chosen, reading is usually the longer half on a
/// large file]. <see cref="Text"/> is one line for the user, for example "Checking container 12 of 27".
/// </summary>
public sealed record IntakeScanProgress(IntakePhase Phase, int Percent, int ContainersFound, string Text);

/// <summary>
/// The outcome of a scan: the read (<see cref="Intake"/>) and the checks (<see cref="Validation"/>,
/// null when the read did not finish). <see cref="Error"/> is one plain sentence; the scan never
/// throws for a bad file. Not saved in project files.
/// </summary>
public sealed record IntakeScanResult(
    IntakeResult? Intake,
    IntakeValidationResult? Validation,
    string? EncodingUsed,
    bool EncodingGuessed,
    long ElapsedMs,
    bool Cancelled,
    string? Error);

/// <summary>
/// One call for the UI: reads an <see cref="IntakeItem"/> (index, detect or markers, encoding) and
/// then checks every container. Runs off the calling thread. Progress comes about every 250 ms from
/// the reader and the validator, and once at the end with 100. A cancel stops at the next line or
/// container and closes the file before this returns.
/// </summary>
public sealed class IntakeScanService(IntakeSettings? settings = null)
{
    private readonly IntakeSettings _settings = settings ?? new IntakeSettings();

    public Task<IntakeScanResult> ScanAsync(IntakeItem item, IProgress<IntakeScanProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Task.Run(() => Scan(item, progress, token));
    }

    private IntakeScanResult Scan(IntakeItem item, IProgress<IntakeScanProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        IntakeScanResult Stop(IntakeResult? intake, IntakeValidationResult? validation, bool cancelled, string? error)
            => new(intake, validation, intake?.EncodingUsed, intake?.EncodingGuessed ?? false, clock.ElapsedMilliseconds, cancelled, error);

        if (item.Size > _settings.MaxFileSizeMb * 1024L * 1024L)
        {
            return Stop(null, null, false,
                $"The file is {(item.Size / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)} MB, above the limit of {_settings.MaxFileSizeMb} MB. Raise the limit or choose a smaller file.");
        }

        progress?.Report(new IntakeScanProgress(IntakePhase.Reading, 0, 0, "Reading the file"));
        var intake = IntakeReader.Read(item, _settings, p => progress?.Report(FromRead(p)), token);
        if (intake.Error is not null || intake.Cancelled)
        {
            return Stop(intake, null, intake.Cancelled, intake.Error);
        }

        var validation = IntakeValidator.Validate(item, intake, new Forward(p => progress?.Report(FromValidation(p))), token, _settings);
        if (validation.Error is null && !validation.Cancelled)
        {
            progress?.Report(new IntakeScanProgress(IntakePhase.Done, 100, intake.Containers.Count, $"Finished: {IntakeText.Count(intake.Containers.Count, "container")} found"));
        }

        return Stop(intake, validation, validation.Cancelled, validation.Error);
    }

    private static IntakeScanProgress FromRead(IntakeProgress p)
    {
        var percent = p.FileSize > 0 ? (int)Math.Min(50, 50 * p.BytesRead / p.FileSize) : 0;
        return p.Phase == IntakePhase.Reading
            ? new IntakeScanProgress(IntakePhase.Reading, percent, p.ContainersFound, "Reading the file")
            : new IntakeScanProgress(IntakePhase.Detecting, 50, p.ContainersFound, $"{IntakeText.Count(p.ContainersFound, "container")} found");
    }

    private static IntakeScanProgress FromValidation(IntakeProgress p)
    {
        var percent = 50 + (p.ContainersFound > 0 ? (int)Math.Min(49, 50L * p.ContainersValidated / p.ContainersFound) : 49);
        var current = Math.Min(p.ContainersValidated + 1, p.ContainersFound);
        return new IntakeScanProgress(IntakePhase.Validating, percent, p.ContainersFound, $"Checking container {current} of {p.ContainersFound}");
    }

    /// <summary>Hands each report on at once, on the reporting thread, so the order is kept.</summary>
    private sealed class Forward(Action<IntakeProgress> report) : IProgress<IntakeProgress>
    {
        public void Report(IntakeProgress value) => report(value);
    }
}
