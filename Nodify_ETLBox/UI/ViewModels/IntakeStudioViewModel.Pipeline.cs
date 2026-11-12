using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>What "Use in pipeline" did on the canvas: <see cref="Changed"/> when a CSV part node got this part, and one sentence.</summary>
public sealed record UsePartOutcome(bool Changed, string Message);

/// <summary>
/// "Use in pipeline" (G6d): makes the selected table part, in the layout as it is on screen, the CSV part of
/// the pipeline. The canvas work is done by the host (<c>MainViewModel.UseCsvPart</c>); the Studio builds the
/// selector, shows the host's sentence and writes <c>pipeline.part-chosen</c>. The file is only read.
/// </summary>
public sealed partial class IntakeStudioViewModel
{
    public const string NoPipelineText = "Open Intake Studio from a File intake node to use a part in a pipeline.";
    public const string SettingsNotSavedText = "Save the settings first: the File intake node still has other settings than this scan.";
    public const string ChoosePartText = "Scan the file and choose a part first.";

    private Func<CsvPartSelector, UsePartOutcome>? _usePart;
    private (IntakeEditSession Session, ContainerInfo Info, string? Problem)? _partCheck;

    public RelayCommand UseInPipelineCommand { get; private set; } = null!;

    /// <summary>True when the window was opened from a File intake node, so the button is shown.</summary>
    public bool CanUsePipeline => _usePart is not null;

    /// <summary>Why "Use in pipeline" is not possible now (the selector's own sentence for a part that is not a table), or what it will do.</summary>
    public string UseInPipelineToolTip => UseInPipelineProblem() ?? $"Make part {SelectedIndex + 1} the CSV part of this pipeline.";

    private void CreatePipelineCommands(Func<CsvPartSelector, UsePartOutcome>? usePart)
    {
        _usePart = usePart;
        UseInPipelineCommand = new RelayCommand(_ => UseInPipeline(), _ => UseInPipelineProblem() is null);
    }

    private void RaisePipelineStates()
    {
        OnPropertyChanged(nameof(UseInPipelineToolTip));
        UseInPipelineCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Null when the selected part can be used; else one sentence. The selector check reads the part's first lines, once per part.</summary>
    private string? UseInPipelineProblem()
    {
        if (_usePart is null)
        {
            return NoPipelineText;
        }

        if (!CanEditSelected)
        {
            return ChoosePartText;
        }

        if (ScannedSettings != _savedSettings)
        {
            return SettingsNotSavedText;
        }

        var session = _session!;
        var info = session.Containers[SelectedIndex];
        if (_partCheck is not { } check || !ReferenceEquals(check.Session, session) || !ReferenceEquals(check.Info, info))
        {
            CsvPartSelector.FromSession(session, SelectedIndex, out var message, encoding: ScannedSettings!.Encoding);
            check = (session, info, message.Length == 0 ? null : message);
            _partCheck = check;
        }

        return check.Problem;
    }

    private void UseInPipeline()
    {
        if (UseInPipelineProblem() is { } problem)
        {
            EditStatus = problem;
            return;
        }

        var session = _session!;
        var selector = CsvPartSelector.FromSession(session, SelectedIndex, out var message, encoding: ScannedSettings!.Encoding);
        if (selector is null)
        {
            EditStatus = message;
            return;
        }

        var outcome = _usePart!(selector);
        EditStatus = outcome.Message;
        if (outcome.Changed)
        {
            _lineage?.Write(new LineageEvent("pipeline.part-chosen", session.IntakeId, session.FileName, session.Sha256, new Dictionary<string, object?>
            {
                ["templateId"] = selector.Template.Id.ToString("D"),
                ["templateName"] = selector.Template.Name,
                ["partNumber"] = selector.PartIndex + 1
            }));
            ShowLineageNote();
        }
    }
}
