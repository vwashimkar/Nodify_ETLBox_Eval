using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Checks each container of an <see cref="IntakeResult"/> by its format (plan section 13, stage
/// G3). Each container is read again by its offsets, at most <see cref="ValidationSettings.MaxValidationMb"/>
/// of it, never the whole file. Problems come back as issues with line numbers in the file, and a
/// missing or locked file as <see cref="IntakeValidationResult.Error"/>, never as an exception.
/// </summary>
public static class IntakeValidator
{
    private const int ChunkSize = 64 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly char[] KeyValueDelimiters = [',', ';', '|', '\t'];

    /// <summary>Runs <see cref="Validate"/> off the calling thread.</summary>
    public static Task<IntakeValidationResult> ValidateAsync(IntakeItem item, IntakeResult result, IProgress<IntakeProgress>? progress, CancellationToken token, IntakeSettings? settings = null)
        => Task.Run(() => Validate(item, result, progress, token, settings));

    public static IntakeValidationResult Validate(IntakeItem item, IntakeResult result, IProgress<IntakeProgress>? progress, CancellationToken token, IntakeSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(result);
        settings ??= new IntakeSettings();

        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var done = new List<ContainerValidation>();
        var totals = new int[3];
        long bytesChecked = 0;
        var cancelled = false;
        string? error = null;
        var fileSize = result.File?.Size ?? item.Size;
        var total = result.Containers.Count;

        var encoding = IntakeReader.ResolveEncoding(result.File?.Encoding ?? settings.EncodingName, out var encodingError, allowUtf16: true);
        if (encoding is null)
        {
            error = encodingError;
        }
        else
        {
            Stream? stream = null;
            try
            {
                foreach (var container in result.Containers)
                {
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    var collector = new IssueCollector(settings.Validation.MaxIssuesPerContainer);
                    var read = ReadContainer(item, ref stream, container, settings.Validation, encoding, token, collector);
                    if (read is null && token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    if (read is not null)
                    {
                        bytesChecked += read.Value.Bytes;
                        Check(container, read.Value, settings, collector, token);
                    }

                    var validation = collector.ToValidation(container.Order);
                    done.Add(validation);
                    for (var s = 0; s < 3; s++)
                    {
                        totals[s] += collector.BySeverity[s];
                    }

                    if (clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = clock.Elapsed;
                        progress?.Report(new IntakeProgress(bytesChecked, fileSize, total, IntakePhase.Validating, done.Count));
                    }
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                error = LineIndexer.NotFoundMessage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = LineIndexer.UnreadableMessage;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        progress?.Report(new IntakeProgress(bytesChecked, fileSize, total, cancelled || error is not null ? IntakePhase.Validating : IntakePhase.Done, done.Count));
        return new IntakeValidationResult(Guid.NewGuid(), item.Id, done, totals[(int)ValidationSeverity.Error], totals[(int)ValidationSeverity.Warning],
            totals[(int)ValidationSeverity.Info], clock.ElapsedMilliseconds, cancelled, error);
    }

    // ---- Reading ------------------------------------------------------------------------------

    /// <summary>The container's text, cut to whole lines, and what the read covered.</summary>
    private readonly record struct ContainerText(string[] Lines, long FirstLine, long Bytes, bool Truncated, byte[] Raw, Encoding Encoding);

    /// <summary>Reads the container's bytes by offsets, at most the limit. Null when cancelled or when the container is empty.</summary>
    private static ContainerText? ReadContainer(IntakeItem item, ref Stream? stream, ContainerInfo container, ValidationSettings settings, Encoding encoding,
        CancellationToken token, IssueCollector issues)
    {
        if (container.ContentStartByte is not { } start || container.ContentEndByte is not { } end || container.ContentFirstLine is not { } firstLine)
        {
            issues.Add(ValidationSeverity.Info, "container.empty", "This container has no lines, so there is nothing to check.", container.MarkerBeginLine);
            return null;
        }

        var limit = settings.MaxValidationMb * 1024L * 1024L;
        var truncated = end - start > limit;
        var length = truncated ? limit : end - start;
        if (encoding is UnicodeEncoding)
        {
            length -= length % 2;
        }

        if (stream is null || !stream.CanSeek)
        {
            stream?.Dispose();
            stream = item.OpenRead();
            IntakeReader.MoveTo(stream, start);
        }
        else
        {
            stream.Seek(start, SeekOrigin.Begin);
        }

        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            var read = stream.Read(bytes, offset, (int)Math.Min(ChunkSize, length - offset));
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (!stream.CanSeek)
        {
            // Forward-only: the next container opens the item again.
            stream.Dispose();
            stream = null;
        }

        var text = encoding.GetString(bytes, 0, offset).TrimStart('﻿');
        var lines = Regex.Split(text, "\r\n|\n|\r");
        if (truncated && lines.Length > 1)
        {
            // The last line may be cut in the middle.
            lines = lines[..^1];
            issues.Add(ValidationSeverity.Warning, "validate.partial",
                $"This container is larger than {settings.MaxValidationMb} MB, so only its first {settings.MaxValidationMb} MB were checked.", firstLine);
        }

        return new ContainerText(lines, firstLine, offset, truncated, bytes[..offset], encoding);
    }

    // ---- Checks -------------------------------------------------------------------------------

    private static void Check(ContainerInfo container, ContainerText text, IntakeSettings settings, IssueCollector issues, CancellationToken token)
    {
        CheckCharacters(text, issues);
        switch (container.Format)
        {
            case "json" or "badgerfish-json":
                CheckJson(text, settings.Validation, issues, token);
                break;
            case "json-lines":
                CheckJsonLines(text, issues, token);
                break;
            case "xml" or "soap":
                CheckXml(text, settings.Validation, issues);
                break;
            case "csv" or "tsv":
                CheckTable(container, text, issues, token);
                break;
            case "keyvalue":
                CheckKeyValue(container, text, settings.Detector.MaxKeyLength, issues);
                break;
            case "yaml":
                CheckYaml(text, settings.Validation, issues, token);
                break;
            case "text":
                break;
            default:
                issues.Add(ValidationSeverity.Info, "format.unchecked", $"There are no checks for the format {container.Format} yet.", text.FirstLine);
                break;
        }
    }

    private static void CheckCharacters(ContainerText text, IssueCollector issues)
    {
        long bad = 0;
        long? first = null;
        for (var i = 0; i < text.Lines.Length; i++)
        {
            var count = text.Lines[i].Count(c => c is '�' or '\0');
            if (count > 0)
            {
                bad += count;
                first ??= text.FirstLine + i;
            }
        }

        if (bad > 0)
        {
            issues.Add(ValidationSeverity.Warning, "encoding.bad-characters", $"{bad} characters could not be read correctly (wrong encoding?).", first);
        }
    }

    private static byte[] Utf8Of(ContainerText text)
    {
        if (text.Encoding.CodePage != Encoding.UTF8.CodePage)
        {
            return Encoding.UTF8.GetBytes(string.Join("\n", text.Lines));
        }

        var raw = text.Raw;
        return raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? raw[3..] : raw;
    }

    /// <summary>
    /// One pass of System.Text.Json's reader with the depth limit: it stops at the first syntax
    /// error and finds duplicate property names per object. The reader does not recurse, so deep
    /// nesting cannot overflow the stack.
    /// </summary>
    private static void CheckJson(ContainerText text, ValidationSettings settings, IssueCollector issues, CancellationToken token)
    {
        var bytes = Utf8Of(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = settings.JsonMaxDepth, CommentHandling = JsonCommentHandling.Disallow });
        var objects = new Stack<HashSet<string>?>();
        var lineCounter = new LineCounter(bytes);
        var tokens = 0;
        try
        {
            while (reader.Read())
            {
                if ((++tokens & 4095) == 0 && token.IsCancellationRequested)
                {
                    return;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        objects.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName when objects.Peek() is { } names:
                        var name = reader.GetString() ?? string.Empty;
                        if (!names.Add(name))
                        {
                            issues.Add(ValidationSeverity.Warning, "json.duplicate-key",
                                $"The property name '{Cut(name)}' appears twice in the same object; most readers keep only the last value.",
                                text.FirstLine + lineCounter.LineOf(reader.TokenStartIndex));
                        }

                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            var line = text.FirstLine + (ex.LineNumber ?? 0);
            if (text.Truncated && line >= text.FirstLine + text.Lines.Length - 1)
            {
                return;
            }

            var depth = ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);
            issues.Add(ValidationSeverity.Error, depth ? "json.depth" : "json.syntax",
                depth ? $"The JSON is nested deeper than {settings.JsonMaxDepth} levels, so it was not checked further."
                    : $"The JSON is not valid here: {CleanJsonReason(ex.Message)}.",
                line, (int)(ex.BytePositionInLine ?? 0) + 1);
        }
    }

    private static void CheckJsonLines(ContainerText text, IssueCollector issues, CancellationToken token)
    {
        for (var i = 0; i < text.Lines.Length; i++)
        {
            if ((i & 1023) == 0 && token.IsCancellationRequested)
            {
                return;
            }

            var line = text.Lines[i];
            if (line.Trim().Length == 0 || IsOneJsonObject(line))
            {
                continue;
            }

            issues.Add(ValidationSeverity.Error, "jsonl.line", "This line is not one complete JSON object.", text.FirstLine + i);
        }
    }

    private static bool IsOneJsonObject(string line)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// XmlReader with DTD processing prohibited, no resolver and a character limit, so nothing is
    /// fetched and no entity is expanded. A DOCTYPE stops the check. A SOAP Body holding a Fault is
    /// a warning with the fault text.
    /// </summary>
    private static void CheckXml(ContainerText text, ValidationSettings settings, IssueCollector issues)
    {
        var doctype = Array.FindIndex(text.Lines, l => l.Contains("<!DOCTYPE", StringComparison.Ordinal));
        if (doctype >= 0)
        {
            issues.Add(ValidationSeverity.Error, "xml.doctype", "DOCTYPE declarations are not read, so this XML was not checked.", text.FirstLine + doctype);
            return;
        }

        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = settings.XmlMaxCharacters,
            MaxCharactersFromEntities = 1024,
            IgnoreWhitespace = true
        };

        string? soapNamespace = null;
        var inFault = false;
        long? faultLine = null;
        string? faultText = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(string.Join("\n", text.Lines)), readerSettings);
            var info = (IXmlLineInfo)reader;
            var more = reader.Read();
            while (more)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Depth == 0 && reader.LocalName == "Envelope" && reader.NamespaceURI is SoapNamespaces.V11 or SoapNamespaces.V12)
                    {
                        soapNamespace = reader.NamespaceURI;
                    }
                    else if (soapNamespace is not null && reader.Depth == 2 && reader.LocalName == "Fault" && reader.NamespaceURI == soapNamespace)
                    {
                        inFault = true;
                        faultLine = text.FirstLine + info.LineNumber - 1;
                    }
                    else if (inFault && faultText is null && (reader.LocalName == "faultstring" || (reader.LocalName == "Text" && reader.NamespaceURI == soapNamespace)))
                    {
                        faultText = reader.ReadElementContentAsString();
                        more = !reader.EOF;
                        continue;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 2 && inFault)
                {
                    inFault = false;
                }

                more = reader.Read();
            }
        }
        catch (XmlException ex)
        {
            var line = text.FirstLine + Math.Max(ex.LineNumber, 1) - 1;
            if (text.Truncated && line >= text.FirstLine + text.Lines.Length - 1)
            {
                return;
            }

            issues.Add(ValidationSeverity.Error, "xml.syntax", $"The XML is not well formed here: {CleanXmlReason(ex.Message, text.FirstLine)}.", line,
                ex.LinePosition > 0 ? ex.LinePosition : null);
            return;
        }

        if (faultLine is not null)
        {
            var reason = faultText is null ? "no fault text" : Cut(Regex.Replace(faultText, @"\s+", " ").Trim(), settings.MaxFaultLength);
            issues.Add(ValidationSeverity.Warning, "soap.fault", $"SOAP fault: {reason}", faultLine);
        }
    }

    /// <summary>
    /// A csv or tsv container with its own delimiter, skip lines and header guess: rows are counted
    /// quote-aware (a quoted field may span lines), with trailing empty fields left out as the detector does.
    /// </summary>
    private static void CheckTable(ContainerInfo container, ContainerText text, IssueCollector issues, CancellationToken token)
    {
        var delimiter = container.Delimiter is { Length: 1 } d ? d[0] : container.Format == "tsv" ? '\t' : ',';
        char[] delimiters = [delimiter];
        int? expected = null;
        var expectedRaw = 0;
        var basis = "the first data row";
        var dataRows = 0;
        var header = container.HeaderGuess == HeaderGuess.Yes;

        for (var i = Math.Min(container.SkipLines, text.Lines.Length); i < text.Lines.Length; i++)
        {
            if ((i & 1023) == 0 && token.IsCancellationRequested)
            {
                return;
            }

            var first = i;
            var row = text.Lines[i];
            if (row.Trim().Length == 0 || RowShape.IsWeak(row, delimiters))
            {
                continue;
            }

            while (RowShape.EndsInsideQuotes(row, delimiters) && i + 1 < text.Lines.Length)
            {
                row += "\n" + text.Lines[++i];
            }

            if (RowShape.EndsInsideQuotes(row, delimiters))
            {
                if (!text.Truncated)
                {
                    issues.Add(ValidationSeverity.Error, "csv.unterminated-quote", "A quoted field that starts on this line is never closed.", text.FirstLine + first,
                        lineCount: i - first + 1);
                }

                break;
            }

            var fields = RowShape.Split(row, delimiter);
            var count = RowShape.Summary(fields).Count;
            if (header)
            {
                header = false;
                expected = count;
                expectedRaw = fields.Count;
                basis = "the header";
                CheckHeaderNames(fields.Take(count).ToList(), text.FirstLine + first, issues);
                continue;
            }

            dataRows++;
            if (expected is null)
            {
                expected = count;
                expectedRaw = fields.Count;
                continue;
            }

            // As in the detector: equal without trailing empty fields, or equal with them (rows padded to one width).
            if (count != expected && fields.Count != expectedRaw)
            {
                issues.Add(ValidationSeverity.Warning, "csv.field-count", $"This row has {count} fields, but {basis} has {expected}.", text.FirstLine + first,
                    lineCount: i > first ? i - first + 1 : null);
            }
        }

        if (dataRows == 0)
        {
            issues.Add(ValidationSeverity.Warning, "csv.no-rows", "This table has no data rows.", text.FirstLine);
        }
    }

    private static void CheckHeaderNames(List<string> names, long line, IssueCollector issues)
    {
        var empty = names.Select((n, i) => (n, i)).Where(x => x.n.Length == 0).Select(x => x.i + 1).ToList();
        if (empty.Count > 0)
        {
            issues.Add(ValidationSeverity.Warning, "csv.header-empty", $"The header has no name for column {string.Join(", ", empty)}.", line);
        }

        foreach (var duplicate in names.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            issues.Add(ValidationSeverity.Warning, "csv.header-duplicate", $"The column name '{Cut(duplicate.Key)}' appears {duplicate.Count()} times in the header.", line);
        }
    }

    private static void CheckKeyValue(ContainerInfo container, ContainerText text, int maxKeyLength, IssueCollector issues)
    {
        var form = container.KeyValueForm ?? KeyValueForm.Colon;
        var delimiter = container.Delimiter is { Length: 1 } d ? d[0] : form == KeyValueForm.Tab ? '\t' : ',';
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = Math.Min(container.SkipLines, text.Lines.Length); i < text.Lines.Length; i++)
        {
            var line = text.Lines[i];
            var number = text.FirstLine + i;
            if (line.Trim().Length == 0 || RowShape.IsWeak(line, KeyValueDelimiters))
            {
                continue;
            }

            var pairs = PairsOf(line, form, delimiter, maxKeyLength);
            if (pairs is null)
            {
                issues.Add(ValidationSeverity.Warning, "kv.line-form", "This line does not have the key and value form of the other lines.", number);
                continue;
            }

            foreach (var (key, value) in pairs)
            {
                if (seen.TryGetValue(key, out var firstLine))
                {
                    issues.Add(ValidationSeverity.Warning, "kv.duplicate-key", $"The key '{Cut(key)}' appears again; it is first on line {firstLine}.", number);
                }
                else
                {
                    seen[key] = number;
                }

                if (value.Length == 0)
                {
                    issues.Add(ValidationSeverity.Info, "kv.empty-value", $"The key '{Cut(key)}' has no value.", number);
                }
            }
        }
    }

    /// <summary>The keys and values of one line in the container's form, or null when the line does not fit the form.</summary>
    private static List<(string Key, string Value)>? PairsOf(string line, KeyValueForm form, char delimiter, int maxKeyLength)
    {
        static string Key(string key) => key.Trim().TrimEnd(':').Trim();

        switch (form)
        {
            case KeyValueForm.Quoted or KeyValueForm.Delimited or KeyValueForm.Tab:
                var fields = RowShape.Split(line, form == KeyValueForm.Tab ? '\t' : delimiter);
                var count = RowShape.Summary(fields).Count;
                return count >= 2 && Key(fields[0]).Length > 0
                    ? [(Key(fields[0]), string.Join(" ", fields.Skip(1).Take(count - 1).Where(f => f.Length > 0)))]
                    : null;
            default:
                var lineForm = RowShape.FormOf(line, RowShape.Summaries(line, KeyValueDelimiters), KeyValueDelimiters, maxKeyLength);
                if (!RowShape.SameKeyValueFamily(lineForm, form) && lineForm != form)
                {
                    return null;
                }

                if (lineForm == KeyValueForm.Pairs)
                {
                    return RowShape.Split(line, ',').Where(p => p.Contains(':')).Select(p => (Key(p[..p.IndexOf(':')]), p[(p.IndexOf(':') + 1)..].Trim())).ToList();
                }

                var body = lineForm == KeyValueForm.EqualsSign && line.StartsWith("* ", StringComparison.Ordinal) ? line[2..] : line;
                var at = body.IndexOf(lineForm == KeyValueForm.EqualsSign ? '=' : ':');
                return at > 0 ? [(Key(body[..at]), body[(at + 1)..].Trim())] : null;
        }
    }

    private static void CheckYaml(ContainerText text, ValidationSettings settings, IssueCollector issues, CancellationToken token)
    {
        var outcome = YamlCheck.Check(new StringReader(string.Join("\n", text.Lines)), settings, token);
        if (outcome.LimitReason is { } limit)
        {
            issues.Add(ValidationSeverity.Error, "yaml.limit", $"The YAML was not checked to the end, because {limit}.", text.FirstLine);
        }
        else if (!outcome.Parsed && !(text.Truncated && outcome.ErrorLine >= text.Lines.Length))
        {
            issues.Add(ValidationSeverity.Error, "yaml.syntax", $"The YAML is not valid here: {outcome.ErrorReason}.", text.FirstLine + (outcome.ErrorLine ?? 1) - 1,
                outcome.ErrorColumn);
        }
    }

    // ---- Message text -------------------------------------------------------------------------

    /// <summary>The reader's reason without its position and path suffixes.</summary>
    private static string CleanJsonReason(string message)
    {
        var reason = message;
        foreach (var marker in new[] { " LineNumber:", " Path:", " | " })
        {
            var at = reason.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                reason = reason[..at];
            }
        }

        reason = reason.Trim().TrimEnd('.');
        return reason.Length == 0 ? "the text is not valid JSON" : reason;
    }

    /// <summary>The parser's reason with line numbers moved into the file's numbering and the trailing position removed.</summary>
    private static string CleanXmlReason(string message, long firstLine)
    {
        var reason = Regex.Replace(message, @"\s*Line \d+, position \d+\.?\s*$", string.Empty);
        reason = Regex.Replace(reason, @"\bline (\d+)", m => $"line {firstLine + long.Parse(m.Groups[1].Value) - 1}");
        reason = reason.Trim().TrimEnd('.');
        return reason.Length == 0 ? "a tag or character is wrong" : reason;
    }

    private static string Cut(string value, int max = 60) => value.Length <= max ? value : value[..max] + "...";

    /// <summary>Line numbers (0-based) of byte offsets that only grow.</summary>
    private sealed class LineCounter(byte[] bytes)
    {
        private long _index;
        private long _line;

        public long LineOf(long offset)
        {
            for (; _index < offset && _index < bytes.Length; _index++)
            {
                if (bytes[_index] == (byte)'\n')
                {
                    _line++;
                }
            }

            return _line;
        }
    }
}

internal static class SoapNamespaces
{
    public const string V11 = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string V12 = "http://www.w3.org/2003/05/soap-envelope";
}
