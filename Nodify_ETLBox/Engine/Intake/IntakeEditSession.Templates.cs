namespace EtlDesigner.Engine.Intake;

/// <summary>The outcome of <see cref="IntakeEditSession.SaveAsTemplate"/>: the edit result and, when it worked, the new template's id.</summary>
public sealed record TemplateSaveResult(EditResult Result, Guid? TemplateId);

/// <summary>
/// The outcome of <see cref="IntakeEditSession.ApplyTemplate"/>. <see cref="Message"/> is one plain
/// sentence. <see cref="Skipped"/> says, per template part that could not be placed, why; those
/// lines stay as the detector found them.
/// </summary>
public sealed record TemplateApplyResult(bool Ok, string Message, int PartsApplied, int PartsTotal, IReadOnlyList<string> Skipped);

/// <summary>Layout templates (G5c1): save the corrected layout as a template, suggest templates for the automatic layout, apply one.</summary>
public sealed partial class IntakeEditSession
{
    private IReadOnlyList<PartSignature>? _scanSignatures;

    /// <summary>The fingerprint of the scan's automatic layout (never of the edited one). Reads the first line of each csv and tsv part once.</summary>
    public IntakeFingerprint AutomaticFingerprint(TemplateSettings? settings = null) => IntakeFingerprint.Of(ScanSignatures(), settings);

    /// <summary>
    /// Saves the current layout as a template named <paramref name="name"/> (1 to 80 characters, the
    /// rules of container names). Writes <c>template.saved</c> (id, name, parts) to the lineage log.
    /// </summary>
    public TemplateSaveResult SaveAsTemplate(string name, ITemplateStore store, TemplateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (IntakeEditRules.NameProblem(name) is { } problem)
        {
            return new TemplateSaveResult(EditResult.Fail(problem), null);
        }

        settings ??= new TemplateSettings();
        var template = BuildTemplate(name.Trim(), settings);
        var saved = store.Save(template);
        if (!saved.Ok)
        {
            return new TemplateSaveResult(EditResult.Fail(saved.Message), null);
        }

        Log("template.saved", new() { ["templateId"] = template.Id.ToString("D"), ["templateName"] = template.Name, ["parts"] = template.Parts.Count });
        return new TemplateSaveResult(EditResult.Done, template.Id);
    }

    /// <summary>
    /// The best templates of <paramref name="store"/> for the scan's automatic layout (at most three,
    /// best first). Each Exact or Close candidate is written to the lineage log as <c>template.suggested</c>.
    /// </summary>
    public IReadOnlyList<TemplateCandidate> SuggestTemplates(ITemplateStore store, TemplateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        var candidates = IntakeTemplateMatcher.Match(AutomaticFingerprint(settings), store.List(), settings);
        foreach (var c in candidates.Where(c => c.Match != TemplateMatch.None))
        {
            Log("template.suggested", new()
            {
                ["templateId"] = c.Template.Id.ToString("D"),
                ["templateName"] = c.Template.Name,
                ["similarity"] = c.Similarity,
                ["match"] = c.Match.ToString()
            });
        }

        return candidates;
    }

    /// <summary>
    /// Builds the layout <paramref name="template"/> describes on this file, starting from the scan's
    /// automatic layout, and makes it the current layout as ONE undoable step. Each template part is
    /// placed on the next automatic parts with the same formats and delimiters (merging them when the
    /// template merged them), or, for a part that began inside an automatic part, by a split at the
    /// first line equal to its anchor. Format, options and name follow the template. A part that
    /// cannot be placed is skipped and its lines stay as the detector found them. Refused, with
    /// nothing changed, when the template's fingerprint does not match (None).
    /// </summary>
    public TemplateApplyResult ApplyTemplate(IntakeTemplate template, TemplateSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        settings ??= new TemplateSettings();
        var id = template.Id.ToString("D");
        var match = IntakeTemplateMatcher.Match(AutomaticFingerprint(settings), [template], settings).Single();
        if (match.Match == TemplateMatch.None)
        {
            var reason = $"The saved layout '{IntakeEditRules.Cut(template.Name)}' does not fit this file, so nothing was changed.";
            Log("template.rejected", new() { ["templateId"] = id, ["templateName"] = template.Name, ["reason"] = "no match" });
            return new TemplateApplyResult(false, reason, 0, template.Parts.Count, []);
        }

        var auto = _scanContainers;
        var signatures = ScanSignatures();
        var layout = new List<ContainerInfo>(auto.Count);
        var checks = new List<ContainerValidation?>(auto.Count);
        var skipped = new List<string>();
        var applied = 0;
        var next = 0;
        var lastPlaced = false;

        void CopyAuto(int to)
        {
            for (; next < to; next++)
            {
                layout.Add(auto[next]);
                checks.Add(_scanValidation[next]);
            }
        }

        foreach (var part in template.Parts)
        {
            var from = part.From ?? [];
            if (part.StartsInside)
            {
                if (!lastPlaced || part.Anchor is null)
                {
                    skipped.Add(part.Anchor is null ? "no line to split at" : $"no line '{part.Anchor}' found");
                    continue;
                }

                var host = layout[^1];
                if (AnchorLine(host, part.Anchor, settings) is not { } line || SplitParts(host, line, out _) is not { } pieces)
                {
                    skipped.Add($"no line '{part.Anchor}' found");
                    continue;
                }

                layout[^1] = pieces.Top;
                checks[^1] = null;
                var bottom = pieces.Bottom;
                for (var k = 1; k < from.Count && next < auto.Count && Fits(signatures[next], from[k]); k++, next++)
                {
                    bottom = MergedPart(bottom, auto[next], out _) ?? bottom;
                }

                layout.Add(Shape(bottom, part));
                checks.Add(null);
                applied++;
                continue;
            }

            var start = from.Count == 0 ? -1 : Find(signatures, next, from);
            if (start < 0)
            {
                skipped.Add($"no {part.Format} part found");
                lastPlaced = false;
                continue;
            }

            var merged = auto[start];
            for (var k = 1; k < from.Count && merged is not null; k++)
            {
                merged = MergedPart(merged, auto[start + k], out _);
            }

            if (merged is null)
            {
                skipped.Add("its parts could not be merged");
                lastPlaced = false;
                continue;
            }

            CopyAuto(start);
            next = start + from.Count;
            layout.Add(Shape(merged, part));
            checks.Add(null);
            applied++;
            lastPlaced = true;
        }

        CopyAuto(auto.Count);
        for (var i = 0; i < layout.Count; i++)
        {
            layout[i] = layout[i].Order == i + 1 ? layout[i] : layout[i] with { Order = i + 1 };
        }

        var touched = Enumerable.Range(0, layout.Count).Where(i => checks[i] is null).ToList();
        var validation = IntakeValidator.ValidateContainers(Item, layout, touched, EncodingName, CancellationToken.None, _settings);
        if (validation.Error is not null || !Covers(layout, _scanSpans))
        {
            var reason = validation.Error ?? LayoutBrokenMessage;
            Log("template.rejected", new() { ["templateId"] = id, ["templateName"] = template.Name, ["reason"] = "layout not kept" });
            return new TemplateApplyResult(false, reason, 0, template.Parts.Count, []);
        }

        for (var k = 0; k < touched.Count; k++)
        {
            checks[touched[k]] = validation.Containers[k];
        }

        var newV = checks.Select((v, i) => v!.Order == i + 1 ? v : v with { Order = i + 1 }).ToArray();
        var change = new Change("template.applied", 0, [.. _containers], [.. _validation], [.. layout], newV, _state, ++_nextState,
            new() { ["templateId"] = id, ["templateName"] = template.Name });
        Replace(0, change.OldC.Length, change.NewC, change.NewV);
        _state = change.NewState;
        PushUndo(change);
        _redo.Clear();
        Log("template.applied", new()
        {
            ["templateId"] = id,
            ["templateName"] = template.Name,
            ["partsApplied"] = applied,
            ["partsSkipped"] = skipped.Count
        });

        var message = $"Applied '{IntakeEditRules.Cut(template.Name)}': {applied} of {IntakeText.Count(template.Parts.Count, "part")} matched";
        message += skipped.Count == 0 ? "." : $", {skipped.Count} skipped ({string.Join("; ", skipped.Distinct().Take(3))}).";
        return new TemplateApplyResult(true, message, applied, template.Parts.Count, skipped);
    }

    // ---- Template internals ----------------------------------------------------------------------

    private IReadOnlyList<PartSignature> ScanSignatures()
    {
        if (_scanSignatures is null)
        {
            var tables = _scanContainers.Select(c => c.Format is "csv" or "tsv" ? c : c with { ContentStartByte = null }).ToList();
            var facts = PartFacts.Read(Item, tables, EncodingName);
            _scanSignatures = _scanContainers.Select((c, i) => PartFacts.Signature(c, facts[i].Columns)).ToList();
        }

        return _scanSignatures;
    }

    /// <summary>
    /// The template of the current layout. Each part lists the signatures of the automatic parts its
    /// lines overlap; it "starts inside" when its first line is not the first line of the first of them.
    /// </summary>
    private IntakeTemplate BuildTemplate(string name, TemplateSettings settings)
    {
        var autoSignatures = ScanSignatures();
        var facts = PartFacts.Read(Item, _containers, EncodingName);
        var parts = new List<TemplatePart>(_containers.Count);
        var cursor = 0;
        for (var i = 0; i < _containers.Count; i++)
        {
            var c = _containers[i];
            var from = new List<PartSignature>();
            var startsInside = false;
            if (Span(c) is { } span)
            {
                while (cursor < _scanContainers.Count && (Span(_scanContainers[cursor])?.Last ?? 0) < span.First)
                {
                    cursor++;
                }

                for (var k = cursor; k < _scanContainers.Count && Span(_scanContainers[k]) is { } a && a.First <= span.Last; k++)
                {
                    if (from.Count == 0)
                    {
                        startsInside = span.First > a.First;
                    }

                    from.Add(autoSignatures[k]);
                }
            }

            var anchor = PartFacts.Anchor(facts[i].FirstLine, i > 0 ? _containers[i - 1] : null, i > 0 ? facts[i - 1].Columns : null, settings.MaxAnchorLength);
            parts.Add(new TemplatePart(c.Format, c.Name, c.Delimiter, c.HeaderGuess, c.SkipLines, c.KeyValueForm,
                c.Format is "csv" or "tsv" ? facts[i].Columns : null, anchor, from, startsInside));
        }

        return new IntakeTemplate(IntakeTemplate.CurrentFormatVersion, Guid.NewGuid(), name, DateTime.UtcNow, AutomaticFingerprint(settings), parts);
    }

    private static bool Fits(PartSignature found, PartSignature wanted) => found.Format == wanted.Format && found.Delimiter == wanted.Delimiter;

    /// <summary>The first index at or after <paramref name="from"/> where the automatic parts fit <paramref name="wanted"/> in order, or -1.</summary>
    private static int Find(IReadOnlyList<PartSignature> signatures, int from, IReadOnlyList<PartSignature> wanted)
    {
        for (var j = from; j + wanted.Count <= signatures.Count; j++)
        {
            var k = 0;
            while (k < wanted.Count && Fits(signatures[j + k], wanted[k]))
            {
                k++;
            }

            if (k == wanted.Count)
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>The template part's format, options and name on a placed container, by the rules of the edits.</summary>
    private static ContainerInfo Shape(ContainerInfo c, TemplatePart part)
    {
        if (part.Format != c.Format && IntakeEditRules.FormatName(part.Format) is { } format)
        {
            c = WithFormat(c, format);
        }

        var lines = c.ContentLineCount;
        return c with
        {
            Delimiter = IntakeEditRules.IsDelimiterValue(c.Format, part.Delimiter) ? part.Delimiter : c.Delimiter,
            HeaderGuess = IntakeEditRules.IsTable(c.Format) ? part.Header : c.HeaderGuess,
            SkipLines = IntakeEditRules.IsTable(c.Format) || c.Format == "keyvalue" ? (int)Math.Clamp(part.SkipLines, 0, Math.Max(0, lines - 1)) : c.SkipLines,
            KeyValueForm = c.Format == "keyvalue" ? part.KeyValueForm ?? c.KeyValueForm : c.KeyValueForm,
            Name = string.IsNullOrWhiteSpace(part.Name) ? c.Name : part.Name,
            ConfirmedByUser = true,
            Reasons = With(c.Reasons, "From a saved layout.")
        };
    }

    /// <summary>The first line after the container's first line whose trimmed text (cut as anchors are) equals <paramref name="anchor"/>.</summary>
    private long? AnchorLine(ContainerInfo host, string anchor, TemplateSettings settings)
    {
        if (host is not { ContentFirstLine: { } first, ContentStartByte: { } start, ContentEndByte: { } end } || end - start > _edit.MaxEditMb * 1024L * 1024L)
        {
            return null;
        }

        var bytes = ContainerBytes.Read(Item, start, end, out _);
        var encoding = IntakeReader.ResolveEncoding(EncodingName, out _);
        if (bytes is null || encoding is null)
        {
            return null;
        }

        var lines = ContainerBytes.Lines(bytes, start, IsUtf16 ? 2 : 1, EncodingName == IntakeEncodings.Utf16Be);
        for (var i = 1; i < lines.Count; i++)
        {
            var text = encoding.GetString(bytes, (int)(lines[i].Start - start), (int)(lines[i].End - lines[i].Start)).Trim();
            if ((text.Length <= settings.MaxAnchorLength ? text : text[..settings.MaxAnchorLength]) == anchor)
            {
                return first + i;
            }
        }

        return null;
    }
}
