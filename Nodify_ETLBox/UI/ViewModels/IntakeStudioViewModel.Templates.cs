using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using EtlDesigner.Engine.Intake;
using EtlDesigner.UI.Mvvm;

namespace EtlDesigner.UI.ViewModels;

/// <summary>One saved layout in the Templates tab. <see cref="Match"/> is "Exact", "Close" or empty for the current file.</summary>
public sealed record IntakeTemplateRow(Guid Id, string Name, string Parts, string Created, string Match, IntakeTemplate Template);

/// <summary>
/// Layout templates in Intake Studio (G5c2): save the current layout as a template, the list of saved
/// layouts with their match for this file, apply, rename, delete, and the banner that offers a
/// matching saved layout after a scan. Every rule lives here; the window only binds.
/// </summary>
public sealed partial class IntakeStudioViewModel
{
    public const string ApplyAnywayQuestion = "This saved layout does not look like this file. Apply it anyway?";
    public const string TemplateCancelledText = "The saved layout was not applied, so nothing was changed.";

    private readonly ITemplateStore? _templates;
    private readonly TemplateSettings _templateSettings;
    private ObservableCollection<IntakeTemplateRow> _templateRows = new();
    private IReadOnlyList<TemplateCandidate> _candidates = [];
    private IntakeTemplateRow? _selectedTemplate;
    private string _templateName = string.Empty;
    private string? _templateStatus;
    private bool _storeProblemsShown;
    private bool _isTemplateBusy;
    private bool _isBannerVisible;
    private string? _bannerText;
    private TemplateCandidate? _bannerCandidate;
    private CancellationTokenSource? _applyCancel;

    public RelayCommand SaveTemplateCommand { get; private set; } = null!;

    public RelayCommand ApplyTemplateCommand { get; private set; } = null!;

    public RelayCommand DeleteTemplateCommand { get; private set; } = null!;

    public RelayCommand RenameTemplateCommand { get; private set; } = null!;

    public RelayCommand ApplyBannerCommand { get; private set; } = null!;

    public RelayCommand DismissBannerCommand { get; private set; } = null!;

    /// <summary>The template suggestion of the last scan (for tests).</summary>
    public Task? SuggestTask { get; private set; }

    /// <summary>The last template save, apply, rename or delete (for tests).</summary>
    public Task? TemplateTask { get; private set; }

    /// <summary>The saved layouts, by name.</summary>
    public ObservableCollection<IntakeTemplateRow> Templates
    {
        get => _templateRows;
        private set => SetProperty(ref _templateRows, value);
    }

    public IntakeTemplateRow? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                if (value is not null)
                {
                    TemplateName = value.Name;
                }

                RaiseTemplateStates();
            }
        }
    }

    /// <summary>The name box: the name to save or rename to. After a scan, the file name without its extension.</summary>
    public string TemplateName
    {
        get => _templateName;
        set
        {
            if (SetProperty(ref _templateName, value ?? string.Empty))
            {
                RaiseTemplateStates();
            }
        }
    }

    /// <summary>The outcome of the last template action, or a problem of the store. One plain sentence.</summary>
    public string? TemplateStatus
    {
        get => _templateStatus;
        private set => SetProperty(ref _templateStatus, value);
    }

    /// <summary>True while a template is saved, applied, renamed or deleted.</summary>
    public bool IsTemplateBusy
    {
        get => _isTemplateBusy;
        private set
        {
            if (SetProperty(ref _isTemplateBusy, value))
            {
                RaiseEditStates();
            }
        }
    }

    /// <summary>A scan has finished and nothing runs: the Templates tab is usable.</summary>
    public bool CanUseTemplates => _session is not null && _templates is not null && !IsScanning && !IsTemplateBusy;

    public bool IsBannerVisible
    {
        get => _isBannerVisible;
        private set => SetProperty(ref _isBannerVisible, value);
    }

    public string? BannerText
    {
        get => _bannerText;
        private set => SetProperty(ref _bannerText, value);
    }

    /// <summary>The saved layout the banner offers.</summary>
    public TemplateCandidate? BannerCandidate
    {
        get => _bannerCandidate;
        private set => SetProperty(ref _bannerCandidate, value);
    }

    private void CreateTemplateCommands()
    {
        SaveTemplateCommand = new RelayCommand(_ => TemplateTask = SaveTemplateAsync(),
            _ => CanUseTemplates && IntakeEditRules.NameProblem(TemplateName) is null);
        ApplyTemplateCommand = new RelayCommand(_ => TemplateTask = ApplyTemplateAsync(SelectedTemplate!.Template, ask: true),
            _ => CanUseTemplates && SelectedTemplate is not null);
        DeleteTemplateCommand = new RelayCommand(_ => TemplateTask = DeleteTemplateAsync(), _ => CanUseTemplates && SelectedTemplate is not null);
        RenameTemplateCommand = new RelayCommand(_ => TemplateTask = RenameTemplateAsync(),
            _ => CanUseTemplates && SelectedTemplate is not null && IntakeEditRules.NameProblem(TemplateName) is null);
        ApplyBannerCommand = new RelayCommand(_ => TemplateTask = ApplyTemplateAsync(BannerCandidate!.Template, ask: false),
            _ => CanUseTemplates && IsBannerVisible && BannerCandidate is not null);
        DismissBannerCommand = new RelayCommand(_ => DismissBanner(), _ => IsBannerVisible);
        LoadTemplates();
    }

    private void RaiseTemplateStates()
    {
        OnPropertyChanged(nameof(CanUseTemplates));
        foreach (var command in new[] { SaveTemplateCommand, ApplyTemplateCommand, DeleteTemplateCommand, RenameTemplateCommand, ApplyBannerCommand, DismissBannerCommand })
        {
            command?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>After a finished scan: the name box, and the suggestion off the UI thread (the list is already shown).</summary>
    private void AfterScanTemplates(LayoutStart start, int version)
    {
        TemplateName = Path.GetFileNameWithoutExtension(FilePath.Trim());
        if (_templates is null)
        {
            return;
        }

        var session = start.Session;
        SuggestTask = SuggestAsync(session, version, start.PlanApplied);
    }

    private async Task SuggestAsync(IntakeEditSession session, int version, bool planApplied)
    {
        IReadOnlyList<TemplateCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => session.SuggestTemplates(_templates!, _templateSettings));
        }
        catch (Exception ex)
        {
            TryLog(ex);
            return;
        }

        // The await came back to the calling (UI) thread; a newer scan wins.
        if (version != _scanVersion || !ReferenceEquals(session, _session))
        {
            return;
        }

        _candidates = candidates;
        LoadTemplates();
        if (!planApplied && !session.IsModified && !IsScanning && candidates.FirstOrDefault() is { Match: not TemplateMatch.None } best)
        {
            var name = IntakeEditRules.Cut(best.Template.Name);
            BannerText = best.Match == TemplateMatch.Exact
                ? $"A saved layout, '{name}', matches this file."
                : $"A saved layout, '{name}', looks similar to this file." + (best.NamesDiffer ? " " + IntakeTemplateMatcher.NamesDifferReason : "");
            BannerCandidate = best;
            IsBannerVisible = true;
            RaiseTemplateStates();
        }
    }

    /// <summary>Reads the list from the store, keeps the selection by id, and shows the store's problems once.</summary>
    private void LoadTemplates(Guid? select = null)
    {
        if (_templates is null)
        {
            return;
        }

        var keep = select ?? SelectedTemplate?.Id;
        var labels = _candidates.Where(c => c.Match != TemplateMatch.None).ToDictionary(c => c.Template.Id, c => c.Match.ToString());
        Templates = new ObservableCollection<IntakeTemplateRow>(_templates.List().Select(t => new IntakeTemplateRow(
            t.Id, t.Name, IntakeText.Count(t.Parts.Count, "part"), t.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            labels.GetValueOrDefault(t.Id, string.Empty), t)));
        // Kept without the setter: a reload does not change the name box.
        _selectedTemplate = Templates.FirstOrDefault(r => r.Id == keep);
        OnPropertyChanged(nameof(SelectedTemplate));
        RaiseTemplateStates();

        if (!_storeProblemsShown && (_templates.Note is not null || _templates.Skipped.Count > 0))
        {
            _storeProblemsShown = true;
            var count = _templates.Skipped.Count;
            var skipped = count == 0 ? null : count == 1 ? "1 saved layout could not be read." : $"{IntakeText.Count(count, "saved layout")} could not be read.";
            TemplateStatus = string.Join(" ", new[] { _templates.Note, skipped }.Where(t => t is not null));
        }
    }

    private async Task SaveTemplateAsync()
    {
        var session = _session!;
        var name = TemplateName.Trim();
        IsTemplateBusy = true;
        TemplateSaveResult saved;
        try
        {
            await WaitForSuggestion();
            saved = await Task.Run(() => session.SaveAsTemplate(name, _templates!, _templateSettings));
        }
        finally
        {
            IsTemplateBusy = false;
        }

        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        if (!saved.Result.Ok)
        {
            TemplateStatus = saved.Result.Message;
            return;
        }

        LoadTemplates(saved.TemplateId);
        TemplateStatus = $"Saved the layout as '{IntakeEditRules.Cut(name)}'.";
    }

    private async Task ApplyTemplateAsync(IntakeTemplate template, bool ask)
    {
        var session = _session!;
        var version = _scanVersion;
        await WaitForSuggestion();
        if (ask && IntakeTemplateMatcher.Match(session.AutomaticFingerprint(_templateSettings), [template], _templateSettings).Single().Match == TemplateMatch.None
            && !(_confirm?.Invoke(ApplyAnywayQuestion) ?? false))
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _applyCancel = cancel;
        IsTemplateBusy = true;
        TemplateApplyResult result;
        try
        {
            result = await Task.Run(() => session.ApplyTemplate(template, _templateSettings));
        }
        catch (Exception ex)
        {
            TryLog(ex);
            result = new TemplateApplyResult(false, "The saved layout could not be applied. Scan the file again and try once more.", 0, 0, []);
        }
        finally
        {
            if (ReferenceEquals(_applyCancel, cancel))
            {
                _applyCancel = null;
            }

            IsTemplateBusy = false;
        }

        // Cancelled (a new scan, or the window closes): the layout stays as it was.
        if (cancel.IsCancellationRequested || version != _scanVersion || !ReferenceEquals(session, _session))
        {
            if (result.Ok)
            {
                session.Undo();
            }

            if (ReferenceEquals(session, _session))
            {
                TemplateStatus = TemplateCancelledText;
            }

            cancel.Dispose();
            return;
        }

        cancel.Dispose();
        TemplateStatus = result.Message;
        if (!result.Ok)
        {
            return;
        }

        HideBanner();
        var changed = RefreshRows();
        if (changed >= 0 && changed < Containers.Count)
        {
            SelectedContainer = Containers[changed];
        }

        AfterLayoutChange();
        EditStatus = result.Message;
    }

    private async Task DeleteTemplateAsync()
    {
        var row = SelectedTemplate!;
        if (!(_confirm?.Invoke($"Delete the saved layout '{IntakeEditRules.Cut(row.Name)}'? This cannot be undone.") ?? false))
        {
            return;
        }

        await WaitForSuggestion();
        var result = _templates!.Delete(row.Id);
        if (!result.Ok)
        {
            TemplateStatus = result.Message;
            LoadTemplates();
            return;
        }

        LogTemplateEvent("template.deleted", row.Id, row.Name);
        _selectedTemplate = null;
        LoadTemplates();
        TemplateStatus = $"Deleted the saved layout '{IntakeEditRules.Cut(row.Name)}'.";
        if (BannerCandidate?.Template.Id == row.Id)
        {
            HideBanner();
        }
    }

    private async Task RenameTemplateAsync()
    {
        var row = SelectedTemplate!;
        var name = TemplateName.Trim();
        await WaitForSuggestion();
        var result = _templates!.Rename(row.Id, name);
        if (!result.Ok)
        {
            TemplateStatus = result.Message;
            return;
        }

        LogTemplateEvent("template.renamed", row.Id, name);
        LoadTemplates(row.Id);
        TemplateStatus = $"Renamed the saved layout to '{IntakeEditRules.Cut(name)}'.";
    }

    private void DismissBanner()
    {
        if (BannerCandidate is { } candidate)
        {
            LogTemplateEvent("template.dismissed", candidate.Template.Id, null);
        }

        HideBanner();
    }

    private void HideBanner()
    {
        if (!IsBannerVisible && BannerCandidate is null)
        {
            return;
        }

        IsBannerVisible = false;
        BannerText = null;
        BannerCandidate = null;
        RaiseTemplateStates();
    }

    /// <summary>A new scan or a new setting: no banner, no match labels, nothing running.</summary>
    private void ClearTemplateResult()
    {
        _applyCancel?.Cancel();
        HideBanner();
        if (_candidates.Count > 0)
        {
            _candidates = [];
            LoadTemplates();
        }
    }

    /// <summary>The suggestion reads the session's scan facts off the UI thread; other template work waits for it.</summary>
    private async Task WaitForSuggestion()
    {
        if (SuggestTask is { } task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                TryLog(ex);
            }
        }
    }

    /// <summary>Template events of the window (the engine writes saved, suggested, applied and rejected). Ids and names only.</summary>
    private void LogTemplateEvent(string name, Guid id, string? templateName)
    {
        if (_lineage is null || _session is not { } session)
        {
            return;
        }

        var details = new Dictionary<string, object?> { ["templateId"] = id.ToString("D") };
        if (templateName is not null)
        {
            details["templateName"] = templateName;
        }

        _lineage.Write(new LineageEvent(name, session.IntakeId, session.FileName, session.Sha256, details));
    }
}
