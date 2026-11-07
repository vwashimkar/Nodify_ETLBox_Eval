using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Auto-detection (plan section 4). Takes the lines of one scan from <see cref="LineIndexer"/> and
/// splits them into containers in file order. Memory does not grow with the file: a region keeps
/// only counts and line numbers, a small window of rows is kept for look-ahead, and a JSON or XML
/// block is checked by reading its bytes again by offsets, never by holding its lines.
/// </summary>
public sealed class AutoDetector
{
    private const string Utf8Name = "utf-8";

    private readonly IntakeItem _item;
    private readonly DetectorSettings _settings;
    private readonly Encoding _settingsEncoding;
    private readonly char[] _delimiters;
    private readonly int _windowSize;
    private readonly List<ContainerInfo> _containers = [];
    private readonly List<IntakeWarning> _warnings = [];
    private readonly Dictionary<string, int> _formatCounts = new(StringComparer.Ordinal);
    private readonly List<Row> _window = [];
    private readonly List<(long Number, long Start, long End, string Text, bool Cut)> _quoted = [];

    private Encoding? _headEncoding;
    private Encoding? _fileEncoding;
    private Candidate? _candidate;
    private Region? _open;
    private int _blankRun;
    private bool _tableClosedInParagraph;
    private int _reofferedWeak;
    private (long FirstLine, long StartByte, string? Name)? _separator;

    public AutoDetector(IntakeItem item, DetectorSettings settings, Encoding settingsEncoding)
    {
        _item = item;
        _settings = settings;
        _settingsEncoding = settingsEncoding;
        _delimiters = settings.DelimiterCandidates.Where(d => d.Length == 1).Select(d => d[0]).ToArray();
        // Title lines and weak lines between them can both precede a table, so room for both.
        _windowSize = 2 * settings.MaxPreambleLines + Math.Max(settings.MinTableLines, settings.MinKeyValueLines) + 2;
    }

    public IReadOnlyList<ContainerInfo> Containers => _containers;

    public IReadOnlyList<IntakeWarning> Warnings => _warnings;

    public int ContainerCount => _containers.Count;

    public void OnLine(in IntakeLine line)
    {
        var text = HeadEncoding().GetString(line.Head);
        var cut = !line.HeadIsWholeLine;

        if (_candidate is not null)
        {
            _candidate.Extend(line.Number, line.EndByte);
            if (_candidate.Feed(text))
            {
                FinishCandidate();
            }

            return;
        }

        if (_quoted.Count > 0)
        {
            AddQuotedLine(line.Number, line.StartByte, line.EndByte, text, cut);
            return;
        }

        if (line.IsBlank)
        {
            OnBlank();
            return;
        }

        if (RowShape.IsSeparator(text, _settings.SeparatorCharacters, _settings.MinSeparatorLength, _settings.MaxTitleNameLength, out var separatorName))
        {
            OnSeparator(line.Number, line.StartByte, line.EndByte, separatorName);
            return;
        }

        if (TryStartCandidate(line.Number, line.StartByte, line.EndByte, text, cut, out var jsonRow))
        {
            return;
        }

        if (jsonRow is not null)
        {
            OnRow(jsonRow);
            return;
        }

        if (RowShape.EndsInsideQuotes(text, _delimiters))
        {
            AddQuotedLine(line.Number, line.StartByte, line.EndByte, text, cut);
            return;
        }

        OnRow(MakeRow(line.Number, line.Number, line.StartByte, line.EndByte, text, cut));
    }

    /// <summary>Ends all open regions, as at the end of a file. Marker mode calls it before each marker segment.</summary>
    public void Break()
    {
        if (_candidate is not null)
        {
            FinishCandidate();
        }

        FlushQuoted();
        EndParagraph();
        CloseOpen();
        _blankRun = 0;
        if (_separator is { } separator)
        {
            // Separator lines with nothing after them: a text container of their own.
            _separator = null;
            Emit("text", separator.FirstLine, _lastSeparatorLine, separator.StartByte, _lastSeparatorEnd, ContainerConfidence.Guessed,
                ["separator lines with no region after them"], separator.Name);
        }
    }

    /// <summary>Call once, after the last line of a scan that reached the end of the file.</summary>
    public void Finish() => Break();

    // ---- Section separators ---------------------------------------------------------------

    private long _lastSeparatorLine;
    private long _lastSeparatorEnd;

    /// <summary>
    /// A separator line ends the region before it and becomes the first skip line of the region
    /// after it; its name names that region. Of several separators in a row the last name wins.
    /// </summary>
    private void OnSeparator(long number, long start, long end, string? name)
    {
        FlushQuoted();
        EndParagraph();
        CloseOpen();
        _blankRun = 0;
        _tableClosedInParagraph = false;
        _separator = _separator is { } open ? open with { Name = name ?? open.Name } : (number, start, name);
        _lastSeparatorLine = number;
        _lastSeparatorEnd = end;
    }

    // ---- JSON and XML blocks -------------------------------------------------------------

    private bool TryStartCandidate(long number, long start, long end, string text, bool cut, out Row? jsonRow)
    {
        jsonRow = null;
        var trimmed = text.TrimStart();
        var isJson = trimmed.StartsWith('{') || trimmed.StartsWith('[');
        var isXml = trimmed.StartsWith("<?xml", StringComparison.Ordinal)
                    || (trimmed.Length > 1 && trimmed[0] == '<' && (char.IsLetter(trimmed[1]) || trimmed[1] == '_'));
        if (!isJson && !isXml)
        {
            return false;
        }

        var candidate = new Candidate(isJson, number, start, end);
        if (!candidate.Feed(text))
        {
            DrainBeforeBlock();
            _candidate = candidate;
            return true;
        }

        // A block of one line: checked from the line itself. When it is not valid, the line is an ordinary row.
        if (cut)
        {
            return false;
        }

        if (isJson)
        {
            if (TryParseJson(Encoding.UTF8.GetBytes(text), out _, out _))
            {
                jsonRow = new Row
                {
                    FirstLine = number, LastLine = number, StartByte = start, EndByte = end, Text = text,
                    Fields = RowShape.Summaries(text, _delimiters), JsonValue = true, JsonObject = trimmed.StartsWith('{')
                };
            }

            return false;
        }

        if (TryParseXml(new StringReader(text), out var subType, out _, out _))
        {
            DrainBeforeBlock();
            EmitXml(candidate, subType);
            return true;
        }

        return false;
    }

    private void FinishCandidate()
    {
        var candidate = _candidate!;
        _candidate = null;

        var length = candidate.EndByte - candidate.StartByte;
        if (length > _settings.MaxValidationMb * 1024L * 1024L)
        {
            Emit(candidate.IsJson ? "json" : "xml", candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte,
                ContainerConfidence.Likely, [$"larger than {_settings.MaxValidationMb} MB, so not parsed; the start and end look like {(candidate.IsJson ? "JSON" : "XML")}"]);
            return;
        }

        var bytes = ReadSpan(candidate.StartByte, candidate.EndByte, out var encoding);
        if (bytes is null)
        {
            EmitText(candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte, ["the block could not be read again to check it"]);
            return;
        }

        if (candidate.IsJson)
        {
            var utf8 = encoding.CodePage == Encoding.UTF8.CodePage ? bytes : Encoding.Convert(encoding, Encoding.UTF8, bytes);
            if (TryParseJson(utf8, out var badgerFish, out var error))
            {
                var reasons = new List<string> { "balanced braces or brackets, and System.Text.Json parses the block" };
                if (badgerFish)
                {
                    reasons.Add("BadgerFish hint (Guessed): keys that start with @ and $ keys");
                }

                Emit("json", candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte, ContainerConfidence.Sure, reasons);
                return;
            }

            EmitText(candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte, [$"starts like JSON but does not parse: {error}"]);
            return;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: false);
        if (TryParseXml(reader, out var xmlSubType, out var xmlError, out var doctype))
        {
            EmitXml(candidate, xmlSubType);
            return;
        }

        if (doctype)
        {
            const string message = "XML with a DOCTYPE is not read: DTD processing is off for safety, and nothing is fetched from the network.";
            _warnings.Add(new IntakeWarning(candidate.FirstLine, message));
            EmitText(candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte, ["has a DOCTYPE, so it is not parsed"],
                ContainerStatus.Warning, message);
            return;
        }

        EmitText(candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte, [$"starts like XML but does not parse: {xmlError}"]);
    }

    private void EmitXml(Candidate candidate, string? subType)
    {
        var reasons = new List<string> { "one root element, and XmlReader parses the block" };
        if (subType is not null)
        {
            reasons.Add("the root is a SOAP Envelope (by its namespace)");
        }

        Emit("xml", candidate.FirstLine, candidate.LastLine, candidate.StartByte, candidate.EndByte, ContainerConfidence.Sure, reasons, subType: subType);
    }

    private static bool TryParseJson(byte[] utf8, out bool badgerFish, out string? error)
    {
        badgerFish = false;
        error = null;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            var at = false;
            var dollar = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString() ?? string.Empty;
                    at |= name.StartsWith('@');
                    dollar |= name == "$";
                }
            }

            badgerFish = at && dollar;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseXml(TextReader text, out string? subType, out string? error, out bool doctype)
    {
        subType = null;
        error = null;
        doctype = false;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Document
        };

        try
        {
            using var reader = XmlReader.Create(text, settings);
            var root = true;
            while (reader.Read())
            {
                if (root && reader.NodeType == XmlNodeType.Element)
                {
                    root = false;
                    if (reader.LocalName == "Envelope" && reader.NamespaceURI is "http://schemas.xmlsoap.org/soap/envelope/" or "http://www.w3.org/2003/05/soap-envelope")
                    {
                        subType = "soap";
                    }
                }
            }

            return true;
        }
        catch (XmlException ex)
        {
            error = ex.Message;
            doctype = ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    /// <summary>Reads the bytes of a block again by offsets, with the file's encoding (the BOM decides).</summary>
    private byte[]? ReadSpan(long start, long end, out Encoding encoding)
    {
        encoding = FileEncoding();
        try
        {
            using var stream = _item.OpenRead();
            IntakeReader.MoveTo(stream, start);
            var bytes = new byte[end - start];
            var read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read == bytes.Length ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The encoding of the file's bytes: its BOM decides, else the setting.</summary>
    private Encoding FileEncoding()
    {
        if (_fileEncoding is not null)
        {
            return _fileEncoding;
        }

        var bom = new byte[3];
        var length = 0;
        try
        {
            using var stream = _item.OpenRead();
            length = stream.ReadAtLeast(bom, 3, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        _fileEncoding = length >= 2 && bom[0] == 0xFF && bom[1] == 0xFE ? new UnicodeEncoding(false, false)
            : length >= 2 && bom[0] == 0xFE && bom[1] == 0xFF ? new UnicodeEncoding(true, false)
            : length == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF ? Encoding.UTF8
            : _settingsEncoding;
        return _fileEncoding;
    }

    /// <summary>The indexer gives UTF-8 heads for a file with a BOM (UTF-16 lines are converted); else the setting's bytes.</summary>
    private Encoding HeadEncoding()
        => _headEncoding ??= FileEncoding() is UnicodeEncoding || FileEncoding().CodePage == Encoding.UTF8.CodePage ? Encoding.UTF8 : _settingsEncoding;

    // ---- Rows -----------------------------------------------------------------------------

    private void AddQuotedLine(long number, long start, long end, string text, bool cut)
    {
        _quoted.Add((number, start, end, text, cut));
        var joined = string.Join("\n", _quoted.Select(q => q.Text));
        if (!RowShape.EndsInsideQuotes(joined, _delimiters))
        {
            var first = _quoted[0];
            _quoted.Clear();
            OnRow(MakeRow(first.Number, number, first.Start, end, joined, cut || first.Cut));
            return;
        }

        if (_quoted.Count >= _settings.MaxQuotedLines)
        {
            FlushQuoted();
        }
    }

    /// <summary>A quote that was never closed: its lines become ordinary rows (or blank lines) one by one.</summary>
    private void FlushQuoted()
    {
        var lines = _quoted.ToList();
        _quoted.Clear();
        foreach (var (number, start, end, text, cut) in lines)
        {
            if (text.Trim(' ', '\t').Length == 0)
            {
                OnBlank();
            }
            else
            {
                OnRow(MakeRow(number, number, start, end, text, cut));
            }
        }
    }

    private Row MakeRow(long first, long last, long start, long end, string text, bool cut)
    {
        var fields = RowShape.Summaries(text, _delimiters);
        var weak = RowShape.IsWeak(text, _delimiters);
        return new Row
        {
            FirstLine = first, LastLine = last, StartByte = start, EndByte = end, Text = text, Cut = cut,
            IsWeak = weak, Fields = fields,
            Form = weak ? null : RowShape.FormOf(text, fields, _delimiters, _settings.MaxKeyLength)
        };
    }

    private void OnBlank()
    {
        EndParagraph();
        if (_open is not null && _open.Kind != RegionKind.Text)
        {
            CloseOpen();
        }

        _blankRun++;
        _tableClosedInParagraph = false;
    }

    private void OnRow(Row row)
    {
        if (_open is { Kind: RegionKind.Text } && _blankRun >= _settings.TextSplitBlankLines)
        {
            CloseOpen();
        }

        _blankRun = 0;
        _window.Add(row);
        Pump(final: false);
    }

    private void EndParagraph() => Pump(final: true);

    /// <summary>A JSON or XML block starts: everything before it is decided now.</summary>
    private void DrainBeforeBlock()
    {
        FlushQuoted();
        EndParagraph();
        CloseOpen();
        _blankRun = 0;
        _tableClosedInParagraph = false;
    }

    // ---- Regions --------------------------------------------------------------------------

    private void Pump(bool final)
    {
        while (_window.Count > 0)
        {
            if (_open is not null && _open.Kind != RegionKind.Text)
            {
                if (_open.TryExtend(_window[0], _settings))
                {
                    _window.RemoveAt(0);
                    continue;
                }

                // Weak lines after the region did not end it; offered again, they may sit above the next table.
                var back = _open.TakeWeakTail();
                CloseOpen(inParagraph: true);
                if (back is not null)
                {
                    _window.InsertRange(0, back);
                    _reofferedWeak = back.Count;
                }

                continue;
            }

            if (_window.Count < _windowSize && !final)
            {
                return;
            }

            var start = DecideStart();
            if (start is null && _reofferedWeak > 0)
            {
                // No table below them: they stay a text container of their own, as in G2.
                var first = _window[0];
                var last = _window[_reofferedWeak - 1];
                _window.RemoveRange(0, _reofferedWeak);
                _reofferedWeak = 0;
                EmitText(first.FirstLine, last.LastLine, first.StartByte, last.EndByte, ["only delimiters, spaces or quotes after the block; such a line cannot end it"]);
                continue;
            }

            _reofferedWeak = 0;
            if (_open is { Kind: RegionKind.Text } && start is null)
            {
                _open.AddText(_window[0]);
                _window.RemoveAt(0);
                continue;
            }

            CloseOpen();
            if (start is null)
            {
                _open = Region.TextFrom(_window[0], _window[0].IsWeak ? "only delimiters, spaces or quotes" : null);
                _window.RemoveAt(0);
                continue;
            }

            if (_tableClosedInParagraph && start.Kind is RegionKind.Table or RegionKind.TableOrKeyValue)
            {
                start.HeaderChanged = true;
            }

            _tableClosedInParagraph = false;

            _open = start;
            _window.RemoveRange(0, start.ConsumedRows);
            if (start.Kind == RegionKind.Json)
            {
                CloseOpen(inParagraph: true);
            }
        }
    }

    /// <summary>
    /// What starts at the first row of the window, or null for text. In the order of plan section
    /// 4: JSON lines, a table (with up to <see cref="DetectorSettings.MaxPreambleLines"/> title
    /// lines), key-value. When a block fits both a table and key-value, both stay open and the one
    /// that breaks first loses.
    /// </summary>
    private Region? DecideStart()
    {
        var w = _window;
        var first = w[0];
        if (first.IsWeak)
        {
            // A weak line may start a region only as a skip line above a table.
            return FindTable() is { Skip: > 0 } weakTable
                ? Region.TableFrom(w, weakTable.Skip, weakTable.Delimiter, weakTable.Count, weakTable.HeaderTolerated, weakTable.Ragged, _delimiters)
                : null;
        }

        if (first.JsonValue)
        {
            return first.JsonObject && w.Count > 1 && w[1].JsonObject ? Region.JsonLinesFrom(first) : Region.JsonFrom(first);
        }

        var table = FindTable();
        var keyValue = FindKeyValue();

        // Several Key: value parts on one line are not table rows, also when commas pad them to a table's width [U: chosen].
        if (keyValue is { Form: KeyValueForm.Pairs })
        {
            return Region.KeyValueFrom(first, keyValue.Value.Form);
        }

        if (table is not null && keyValue is not null && keyValue.Value.Rows > table.Value.Skip)
        {
            return Region.TableOrKeyValue(w, table.Value.Skip, table.Value.Delimiter, table.Value.Count, table.Value.HeaderTolerated, table.Value.Ragged,
                keyValue.Value.Form, _delimiters);
        }

        if (table is not null)
        {
            return Region.TableFrom(w, table.Value.Skip, table.Value.Delimiter, table.Value.Count, table.Value.HeaderTolerated, table.Value.Ragged, _delimiters);
        }

        if (keyValue is not null)
        {
            return Region.KeyValueFrom(first, keyValue.Value.Form);
        }

        return FindFixedWidth() is { } fixedWidth ? Region.FixedWidthFrom(w, fixedWidth.Skip, fixedWidth.Count, fixedWidth.Header) : null;
    }

    /// <summary>
    /// A whitespace-aligned table: at least <see cref="DetectorSettings.MinFixedWidthLines"/> rows
    /// with the same number of tokens (at least <see cref="DetectorSettings.MinFixedWidthColumns"/>),
    /// numeric in at least half of the columns after the first. A first row of only text, or of
    /// consecutive integers, is a header; the integer header may be one token short (a plate grid
    /// has no label above its row labels). Title lines above it are skip lines, as for a table.
    /// </summary>
    private (int Skip, int Count, bool Header)? FindFixedWidth()
    {
        var w = _window;
        for (var skip = 0; skip <= Math.Min(_settings.MaxPreambleLines, w.Count - 1); skip++)
        {
            if (skip > 0 && !IsTitle(w[skip - 1]))
            {
                return null;
            }

            var first = RowShape.Tokens(w[skip].Text);
            if (w[skip].IsWeak || first.Length == 0)
            {
                continue;
            }

            var header = RowShape.IsTokenHeader(first);
            var count = first.Length;
            var dataFrom = header ? skip + 1 : skip;
            if (dataFrom < w.Count && header && RowShape.IsConsecutiveIntegers(first))
            {
                var next = RowShape.Tokens(w[dataFrom].Text).Length;
                if (next != count && Math.Abs(next - count) <= _settings.FieldCountTolerance)
                {
                    count = next;
                }
            }

            if (count < _settings.MinFixedWidthColumns)
            {
                continue;
            }

            var data = new List<string[]>();
            for (var i = dataFrom; i < w.Count && !w[i].IsWeak; i++)
            {
                var tokens = RowShape.Tokens(w[i].Text);
                if (tokens.Length != count)
                {
                    break;
                }

                data.Add(tokens);
            }

            var titlesDiffer = Enumerable.Range(0, skip).All(t => RowShape.Tokens(w[t].Text).Length != count);
            var numericColumns = Enumerable.Range(1, count - 1).Count(j => data.All(r => RowShape.IsNumeric(r[j])));
            if (titlesDiffer && data.Count > 0 && data.Count + (header ? 1 : 0) >= _settings.MinFixedWidthLines && 2 * numericColumns >= count - 1)
            {
                return (skip, count, header);
            }
        }

        return null;
    }



    private (int Skip, int Delimiter, int Count, bool HeaderTolerated, bool Ragged)? FindTable()
    {
        var w = _window;
        var titles = 0;
        var weak = 0;
        for (var skip = 0; skip <= Math.Min(2 * _settings.MaxPreambleLines, w.Count - 1); skip++)
        {
            // Skip lines are up to MaxPreambleLines title lines, with up to as many weak lines among or below them.
            if (skip > 0)
            {
                var above = w[skip - 1];
                titles += above.IsWeak ? 0 : 1;
                weak += above.IsWeak ? 1 : 0;
                if ((!above.IsWeak && !IsTitle(above)) || titles > _settings.MaxPreambleLines || weak > _settings.MaxPreambleLines)
                {
                    return null;
                }
            }

            if (w[skip].IsWeak)
            {
                continue;
            }

            (int Delimiter, int Count, int Length, bool HeaderTolerated, bool Ragged)? best = null;
            for (var d = 0; d < _delimiters.Length; d++)
            {
                var run = TableRun(skip, d);
                if (run is null)
                {
                    continue;
                }

                var (count, length, tolerated, ragged) = run.Value;
                var titlesDiffer = Enumerable.Range(0, skip).All(t => w[t].IsWeak || w[t].Fields[d].Count != count);
                if (!titlesDiffer)
                {
                    continue;
                }

                if (best is null || length > best.Value.Length || (length == best.Value.Length && count > best.Value.Count))
                {
                    best = (d, count, length, tolerated, ragged);
                }
            }

            if (best is not null)
            {
                return (skip, best.Value.Delimiter, best.Value.Count, best.Value.HeaderTolerated, best.Value.Ragged);
            }
        }

        return null;
    }

    /// <summary>
    /// The table that starts at a row: rows with the same field count (at least 2), long enough by
    /// the settings. The first row may differ by the tolerance when the rows after it agree (a
    /// header with a trailing delimiter, for example). Rows shorter than the first row are a ragged
    /// table when each has at least <see cref="DetectorSettings.MinRaggedFields"/> fields and two of
    /// them share a field count, so a block whose every line has its own count stays apart.
    /// </summary>
    private (int Count, int Length, bool HeaderTolerated, bool Ragged)? TableRun(int from, int d)
    {
        var w = _window;
        // Equal when the counts without trailing empty fields agree, or the full counts do (rows padded to the same width).
        int Run(int start, int count)
        {
            var raw = w[start].Fields[d].RawCount;
            var n = 0;
            for (var i = start; i < w.Count && !w[i].IsWeak && (w[i].Fields[d].Count == count || w[i].Fields[d].RawCount == raw); i++)
            {
                n++;
            }

            return n;
        }

        bool LongEnough(int lines, int count)
            => lines >= _settings.MinTableLines || (lines >= _settings.MinShortTableLines && count >= _settings.MinShortTableFields);

        var c = w[from].Fields[d].Count;
        if (c >= 2)
        {
            var exact = Run(from, c);
            if (LongEnough(exact, c))
            {
                return (c, exact, false, false);
            }
        }

        if (from + 1 < w.Count && c >= 2)
        {
            var next = w[from + 1].Fields[d].Count;
            var rest = Run(from + 1, next);
            if (next >= 2 && Math.Abs(next - c) <= _settings.FieldCountTolerance && rest >= _settings.MinShortTableLines && LongEnough(rest + 1, next))
            {
                return (next, rest + 1, true, false);
            }
        }

        if (c > _settings.MinRaggedFields)
        {
            var counts = new List<int>();
            for (var i = from + 1; i < w.Count && !w[i].IsWeak && w[i].Fields[d].Count <= c && w[i].Fields[d].Count >= _settings.MinRaggedFields; i++)
            {
                counts.Add(w[i].Fields[d].Count);
            }

            if (counts.Any(n => n < c) && counts.Count != counts.Distinct().Count() && LongEnough(counts.Count + 1, c))
            {
                return (c, counts.Count + 1, false, true);
            }
        }

        return null;
    }

    /// <summary>A title line is short and not weak; its field count is checked against the table.</summary>
    private bool IsTitle(Row row) => !row.IsWeak && row.Text.Trim().Length < _settings.MaxTitleNameLength;

    private (KeyValueForm Form, int Rows)? FindKeyValue()
    {
        var w = _window;
        if (w[0].Form is not { } form)
        {
            return null;
        }

        // Weak lines inside the block do not end it and do not count.
        var rows = 0;
        for (var i = 0; i < w.Count && (w[i].IsWeak || RowShape.SameKeyValueFamily(w[i].Form, form)); i++)
        {
            rows += w[i].IsWeak ? 0 : 1;
        }

        return rows >= _settings.MinKeyValueLines ? (form, rows) : null;
    }

    private void CloseOpen(bool inParagraph = false)
    {
        var region = _open;
        _open = null;
        if (region is null)
        {
            return;
        }

        var (main, tail) = region.Finish(_settings);
        if (main is not null)
        {
            Emit(main.Value.Format, main.Value.FirstLine, main.Value.LastLine, main.Value.StartByte, main.Value.EndByte, main.Value.Confidence,
                main.Value.Reasons, main.Value.Title, main.Value.Delimiter, main.Value.Header, main.Value.SkipLines, main.Value.Form);
        }

        if (tail is not null)
        {
            EmitText(tail.Value.FirstLine, tail.Value.LastLine, tail.Value.StartByte, tail.Value.EndByte, ["only delimiters, spaces or quotes after the block; such a line cannot end it"]);
        }

        _tableClosedInParagraph = inParagraph && main is { Format: "csv" or "tsv" };
    }

    private void EmitText(long first, long last, long start, long end, List<string> reasons, ContainerStatus status = ContainerStatus.Ok, string message = "")
        => Emit("text", first, last, start, end, ContainerConfidence.Guessed, reasons, status: status, message: message);

    private void Emit(
        string format,
        long first,
        long last,
        long start,
        long end,
        ContainerConfidence confidence,
        List<string> reasons,
        string? title = null,
        string? delimiter = null,
        HeaderGuess? header = null,
        int skipLines = 0,
        KeyValueForm? form = null,
        string? subType = null,
        ContainerStatus status = ContainerStatus.Ok,
        string message = "")
    {
        // Separator lines just before the container are its first skip lines, and a separator's name names it.
        if (_separator is { } separator)
        {
            _separator = null;
            skipLines += (int)(first - separator.FirstLine);
            first = separator.FirstLine;
            start = separator.StartByte;
            title = separator.Name ?? title;
        }

        // <format>-<n>, n counted per format in file order (the marker rule); a table's title names it.
        var count = _formatCounts.GetValueOrDefault(format) + 1;
        _formatCounts[format] = count;
        var name = string.IsNullOrWhiteSpace(title) ? $"{format}-{count}" : title;

        _containers.Add(new ContainerInfo(
            _containers.Count + 1, ContainerKind.Detected, format, name, string.Empty, null, null,
            first, last, last - first + 1, start, end, status, message, confidence,
            subType, delimiter, header, skipLines, form, reasons));
    }

    // ---- Types ----------------------------------------------------------------------------

    private sealed class Candidate(bool isJson, long firstLine, long startByte, long endByte)
    {
        private readonly JsonDepthTracker? _json = isJson ? new JsonDepthTracker() : null;
        private readonly XmlDepthTracker? _xml = isJson ? null : new XmlDepthTracker();

        public bool IsJson { get; } = isJson;

        public long FirstLine { get; } = firstLine;

        public long StartByte { get; } = startByte;

        public long LastLine { get; private set; } = firstLine;

        public long EndByte { get; private set; } = endByte;

        public void Extend(long line, long endByte)
        {
            LastLine = line;
            EndByte = endByte;
        }

        public bool Feed(string line) => _json?.Feed(line) ?? _xml!.Feed(line);
    }
}

internal enum RegionKind
{
    Text,
    Json,
    JsonLines,
    Table,
    KeyValue,
    TableOrKeyValue,
    FixedWidth
}

internal readonly record struct RegionResult(
    string Format,
    long FirstLine,
    long LastLine,
    long StartByte,
    long EndByte,
    ContainerConfidence Confidence,
    List<string> Reasons,
    string? Title,
    string? Delimiter,
    HeaderGuess? Header,
    int SkipLines,
    KeyValueForm? Form);

internal readonly record struct LineRange(long FirstLine, long LastLine, long StartByte, long EndByte);

/// <summary>
/// An open region of the auto-detector. It keeps line numbers, offsets and counts only, so a
/// region of a million lines costs what a region of three lines costs.
/// </summary>
internal sealed class Region
{
    private long _firstLine;
    private long _startByte;
    private long _lastLine;
    private long _endByte;
    private LineRange? _weakTail;
    private List<Row>? _weakRows;
    private long _rows;
    private long _dataLines;
    private bool _tolerated;
    private bool _ragged;
    private bool _headerTolerated;
    private int _delimiter;
    private char _delimiterChar;
    private int _count;
    private int _rawCount;
    private KeyValueForm _form;
    private bool _tableAlive;
    private bool _keyValueAlive;
    private bool _tableBroke;
    private HashSet<string>? _labels;
    private bool _otherText;
    private string? _title;
    private HeaderGuess? _header;
    private readonly List<string> _reasons = [];

    private Region(RegionKind kind, Row first)
    {
        Kind = kind;
        _firstLine = first.FirstLine;
        _startByte = first.StartByte;
        _lastLine = first.LastLine;
        _endByte = first.EndByte;
        _rows = 1;
    }

    public RegionKind Kind { get; private set; }

    public int ConsumedRows { get; private set; } = 1;

    public int SkipLines { get; private set; }

    public bool HeaderChanged { get; set; }

    public static Region TextFrom(Row row, string? reason)
    {
        var region = new Region(RegionKind.Text, row);
        region._reasons.Add(reason ?? "no table, key-value, JSON or XML pattern");
        return region;
    }

    public static Region JsonFrom(Row row)
    {
        var region = new Region(RegionKind.Json, row);
        region._reasons.Add("one line that System.Text.Json parses");
        return region;
    }

    public static Region JsonLinesFrom(Row row)
    {
        var region = new Region(RegionKind.JsonLines, row);
        region._reasons.Add("consecutive lines that are each a complete JSON object");
        return region;
    }

    public static Region KeyValueFrom(Row row, KeyValueForm form)
    {
        var region = new Region(RegionKind.KeyValue, row) { _form = form, _keyValueAlive = true };
        return region;
    }

    public static Region FixedWidthFrom(List<Row> window, int skip, int count, bool header)
    {
        var region = new Region(RegionKind.FixedWidth, window[0])
        {
            _count = count, _header = header ? HeaderGuess.Yes : HeaderGuess.No, SkipLines = (int)window.Take(skip).Sum(r => r.LineCount),
            ConsumedRows = skip + 1, _dataLines = header ? 0 : window[skip].LineCount
        };
        if (skip > 0)
        {
            region._title = window[0].Text.Trim().TrimEnd(':').Trim();
        }

        for (var i = 1; i <= skip; i++)
        {
            region.Take(window[i]);
        }

        return region;
    }

    public static Region TableFrom(List<Row> window, int skip, int delimiter, int count, bool headerTolerated, bool ragged, char[] delimiters)
    {
        var region = new Region(RegionKind.Table, window[0]) { _ragged = ragged };
        region.StartTable(window, skip, delimiter, count, headerTolerated, delimiters);
        return region;
    }

    public static Region TableOrKeyValue(List<Row> window, int skip, int delimiter, int count, bool headerTolerated, bool ragged, KeyValueForm form, char[] delimiters)
    {
        var region = new Region(RegionKind.TableOrKeyValue, window[0]) { _form = form, _keyValueAlive = true, _ragged = ragged };
        region.StartTable(window, skip, delimiter, count, headerTolerated, delimiters);
        return region;
    }

    private void StartTable(List<Row> window, int skip, int delimiter, int count, bool headerTolerated, char[] delimiters)
    {
        _delimiter = delimiter;
        _delimiterChar = delimiters[delimiter];
        _count = count;
        _rawCount = window[skip + (headerTolerated ? 1 : 0)].Fields[delimiter].RawCount;
        _headerTolerated = headerTolerated;
        _tolerated = headerTolerated;
        _tableAlive = true;
        SkipLines = (int)window.Take(skip).Sum(r => r.LineCount);

        if (window.Take(skip).FirstOrDefault(r => !r.IsWeak) is { } titleRow)
        {
            var titleFields = RowShape.Split(titleRow.Text, _delimiterChar).Where(f => f.Length > 0);
            _title = string.Join(" ", titleFields).Trim().TrimEnd(':').Trim();
        }

        var header = window[skip].Fields[delimiter];
        var next = skip + 1 < window.Count ? window[skip + 1].Fields[delimiter] : (FieldSummary?)null;
        _header = (!header.HasNumeric && next is { HasNumeric: true }) || (header.FirstEmpty && header.OthersAllText) ? HeaderGuess.Yes
            : header.HasNumeric ? HeaderGuess.No
            : HeaderGuess.Unknown;

        // With no header, every row is checked for the key-value pattern: distinct labels in the first column.
        // An all-text first row (header Unknown) may still be a header, so that needs exactly 2 fields [U: chosen].
        if (_header == HeaderGuess.No || (_header == HeaderGuess.Unknown && count == 2 && !headerTolerated))
        {
            _labels = new HashSet<string>(StringComparer.Ordinal);
            CheckLabel(window[skip]);
        }

        // The window rows up to the table's first data row belong to the region now.
        for (var i = 1; i <= skip; i++)
        {
            Take(window[i]);
        }

        ConsumedRows = skip + 1;
        _dataLines = window[skip].LineCount;
        if (Kind == RegionKind.TableOrKeyValue && window.Take(skip + 1).Any(r => !r.IsWeak && !RowShape.SameKeyValueFamily(r.Form, _form)))
        {
            _keyValueAlive = false;
        }
    }

    public void AddText(Row row) => Take(row);

    /// <summary>Takes the next row when it fits; false closes the region.</summary>
    public bool TryExtend(Row row, DetectorSettings settings)
    {
        if (row.IsWeak)
        {
            if (Kind is not (RegionKind.Table or RegionKind.KeyValue or RegionKind.TableOrKeyValue))
            {
                return false;
            }

            // Inside only when a real row follows; until then it is a tail that cannot end the region.
            // A short tail keeps its rows, so they can be offered to the next region.
            _weakRows = _weakTail is null ? [row] : _weakRows is { } kept && kept.Count < settings.MaxPreambleLines ? [.. kept, row] : null;
            _weakTail = _weakTail is { } tail ? tail with { LastLine = row.LastLine, EndByte = row.EndByte }
                : new LineRange(row.FirstLine, row.LastLine, row.StartByte, row.EndByte);
            return true;
        }

        switch (Kind)
        {
            case RegionKind.FixedWidth:
                if (RowShape.Tokens(row.Text).Length != _count)
                {
                    return false;
                }

                _dataLines += row.LineCount;
                Take(row);
                return true;
            case RegionKind.JsonLines:
                if (!row.JsonObject || _weakTail is not null)
                {
                    return false;
                }

                Take(row);
                return true;
            case RegionKind.KeyValue:
                if (!RowShape.SameKeyValueFamily(row.Form, _form))
                {
                    return false;
                }

                Take(row);
                return true;
            case RegionKind.Table:
            case RegionKind.TableOrKeyValue:
                var count = row.Fields[_delimiter].Count;
                var exact = count == _count || row.Fields[_delimiter].RawCount == _rawCount;
                // A shorter row with no number under a table with a header is the header of the next table, not a ragged row [U: chosen].
                var ragged = count < _count && count >= settings.MinRaggedFields && !(_header == HeaderGuess.Yes && !row.Fields[_delimiter].HasNumeric);
                var tableFits = _tableAlive && (exact || ragged || (count >= 2 && Math.Abs(count - _count) <= settings.FieldCountTolerance));
                var keyValueFits = Kind == RegionKind.TableOrKeyValue && _keyValueAlive && RowShape.SameKeyValueFamily(row.Form, _form);

                if (Kind == RegionKind.TableOrKeyValue && !tableFits && keyValueFits)
                {
                    // The table broke first: the field count varies, so this is key-value.
                    Kind = RegionKind.KeyValue;
                    _tableAlive = false;
                    _tableBroke = true;
                    Take(row);
                    return true;
                }

                if (!tableFits)
                {
                    return false;
                }

                _ragged |= !exact && ragged;
                _tolerated |= !exact && !ragged;
                _dataLines += row.LineCount;
                CheckLabel(row, settings.MaxKeyValueLabelLines);
                if (Kind == RegionKind.TableOrKeyValue && !keyValueFits)
                {
                    Kind = RegionKind.Table;
                }

                Take(row);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Keeps the labels of the first column while they are distinct and not numbers, and notes a
    /// text value in the other columns. Stops (and frees the labels) at the first row that breaks
    /// the pattern, or when the block is longer than <paramref name="maxLines"/>.
    /// </summary>
    private void CheckLabel(Row row, int maxLines = int.MaxValue)
    {
        if (_labels is null)
        {
            return;
        }

        var fields = RowShape.Split(row.Text, _delimiterChar);
        var label = fields[0];
        if (label.Length == 0 || RowShape.IsNumeric(label) || !_labels.Add(label) || _labels.Count > maxLines)
        {
            _labels = null;
            return;
        }

        _otherText |= fields.Skip(1).Any(f => f.Length > 0 && !RowShape.IsNumeric(f));
    }

    /// <summary>The weak lines at the end of the region, taken out of it, or null when there are none or too many to keep.</summary>
    public List<Row>? TakeWeakTail()
    {
        var rows = _weakRows;
        if (rows is not null)
        {
            _weakTail = null;
            _weakRows = null;
        }

        return rows;
    }

    private void Take(Row row)
    {
        _weakTail = null;
        _weakRows = null;
        _lastLine = row.LastLine;
        _endByte = row.EndByte;
        _rows++;
    }

    /// <summary>The finished container and, when the region ended on lines that cannot end it, those lines as a separate tail.</summary>
    public (RegionResult? Main, LineRange? Tail) Finish(DetectorSettings settings)
    {
        var tail = Kind == RegionKind.Text ? null : _weakTail;
        var reasons = new List<string>(_reasons);
        RegionResult Result(string format, ContainerConfidence confidence, string? title = null, string? delimiter = null, HeaderGuess? header = null, int skip = 0, KeyValueForm? form = null)
            => new(format, _firstLine, _lastLine, _startByte, _endByte, confidence, reasons, title, delimiter, header, skip, form);

        switch (Kind)
        {
            case RegionKind.Json:
                return (Result("json", ContainerConfidence.Sure), tail);
            case RegionKind.JsonLines:
                reasons.Add($"{_rows} lines");
                return (Result("json-lines", ContainerConfidence.Sure), tail);
            case RegionKind.KeyValue:
                reasons.Add($"{_rows} lines of the form {_form}");
                if (_tableBroke)
                {
                    reasons.Add("also started like a table, but the field count varies, so key-value");
                }

                var keyValueConfidence = _rows >= settings.LikelyKeyValueMinLines ? ContainerConfidence.Likely : ContainerConfidence.Guessed;
                return (Result("keyvalue", keyValueConfidence, form: _form), tail);
            case RegionKind.FixedWidth:
                reasons.Add($"{_dataLines} lines with {_count} whitespace-separated tokens, numeric in at least half of the columns after the first");
                if (SkipLines > 0)
                {
                    reasons.Add($"{SkipLines} title line(s) above the table are skip lines");
                }

                reasons.Add(_header == HeaderGuess.Yes ? "header guessed (Guessed): the first row is only text or consecutive integers" : "header guess: No");
                var fixedTitle = SkipLines > 0 && _title is { Length: > 0 } ft && ft.Length < settings.MaxTitleNameLength ? ft : null;
                return (Result("fixedwidth", ContainerConfidence.Likely, fixedTitle, "space", _header, SkipLines), tail);
            case RegionKind.Table or RegionKind.TableOrKeyValue
                when _labels is not null && _labels.Count >= settings.MinKeyValueLines && _otherText && !_tolerated && !_ragged:
                // No header, distinct labels in the first column and text among the values: a key-value block, also with 2 fields.
                reasons.Add($"{_labels.Count} lines with {_count} fields and the same field count");
                reasons.Add("distinct labels in first column, no header");
                var labelForm = Kind == RegionKind.TableOrKeyValue ? _form : _delimiterChar == '\t' ? KeyValueForm.Tab : KeyValueForm.Delimited;
                var labelTitle = SkipLines > 0 && _title is { Length: > 0 } lt && lt.Length < settings.MaxTitleNameLength ? lt : null;
                return (Result("keyvalue", ContainerConfidence.Likely, labelTitle, _delimiterChar.ToString(), skip: SkipLines, form: labelForm), tail);
            case RegionKind.Table:
            case RegionKind.TableOrKeyValue:
                var delimiterName = _delimiterChar == '\t' ? "tab" : $"'{_delimiterChar}'";
                reasons.Add($"{_dataLines} lines with {_count} fields for the delimiter {delimiterName}");
                if (SkipLines > 0)
                {
                    reasons.Add($"{SkipLines} title line(s) above the table are skip lines");
                }

                if (_tolerated)
                {
                    reasons.Add(_headerTolerated ? "the first line has a different field count (a trailing delimiter?)" : "some lines have a field count that differs by the tolerance");
                }

                if (_ragged)
                {
                    reasons.Add("rows shorter than header (the missing fields are at the end)");
                }

                if (HeaderChanged)
                {
                    reasons.Add("follows another table with no blank line between: split where the field count changes (Guessed)");
                }

                if (Kind == RegionKind.TableOrKeyValue)
                {
                    reasons.Add($"also fits key-value ({_form}), but has a header or a first column that is not distinct labels, so a table");
                }

                reasons.Add($"header guess: {_header}");
                var confidence = HeaderChanged ? ContainerConfidence.Guessed
                    : _tolerated || _ragged ? ContainerConfidence.Likely
                    : _dataLines >= settings.SureTableMinLines ? ContainerConfidence.Sure
                    : ContainerConfidence.Guessed;
                var format = _delimiterChar == '\t' ? "tsv" : "csv";
                var title = SkipLines > 0 && _title is { Length: > 0 } t && t.Length < settings.MaxTitleNameLength ? t : null;
                return (Result(format, confidence, title, _delimiterChar.ToString(), _header, SkipLines), tail);
            default:
                if (_rows <= 2)
                {
                    reasons.Add("one or two lines that match nothing else");
                }

                return (Result("text", ContainerConfidence.Guessed), null);
        }
    }
}
