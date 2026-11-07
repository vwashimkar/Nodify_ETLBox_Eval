namespace EtlDesigner.Engine.Intake;

/// <summary>
/// Follows the brace and bracket depth of a JSON block line by line, ignoring braces inside
/// strings. It only finds where the block ends; System.Text.Json decides whether it is JSON.
/// </summary>
internal sealed class JsonDepthTracker
{
    private int _depth;
    private bool _inString;
    private bool _escape;

    public bool Closed { get; private set; }

    /// <summary>Reads one line; true when the depth came back to 0 on it.</summary>
    public bool Feed(string line)
    {
        foreach (var c in line)
        {
            if (Closed)
            {
                break;
            }

            if (_inString)
            {
                if (_escape)
                {
                    _escape = false;
                }
                else if (c == '\\')
                {
                    _escape = true;
                }
                else if (c == '"')
                {
                    _inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    _inString = true;
                    break;
                case '{' or '[':
                    _depth++;
                    break;
                case '}' or ']':
                    _depth--;
                    Closed = _depth <= 0;
                    break;
            }
        }

        return Closed;
    }
}

/// <summary>
/// Follows the element depth of an XML block line by line: tags, quoted attributes, comments,
/// CDATA, processing instructions and a DOCTYPE. It only finds where the root element closes;
/// XmlReader decides whether it is XML.
/// </summary>
internal sealed class XmlDepthTracker
{
    private enum Mode
    {
        Content,
        Tag,
        Comment,
        CData,
        Instruction,
        Doctype
    }

    private Mode _mode;
    private int _depth;
    private bool _closingTag;
    private char _quote;
    private int _doctypeBrackets;
    private bool _rootSeen;

    public bool Closed { get; private set; }

    public bool Feed(string line)
    {
        var i = 0;
        while (i < line.Length && !Closed)
        {
            var c = line[i];
            switch (_mode)
            {
                case Mode.Comment:
                    if (At(line, i, "-->"))
                    {
                        _mode = Mode.Content;
                        i += 2;
                    }

                    break;
                case Mode.CData:
                    if (At(line, i, "]]>"))
                    {
                        _mode = Mode.Content;
                        i += 2;
                    }

                    break;
                case Mode.Instruction:
                    if (At(line, i, "?>"))
                    {
                        _mode = Mode.Content;
                        i += 1;
                    }

                    break;
                case Mode.Doctype:
                    if (c == '[')
                    {
                        _doctypeBrackets++;
                    }
                    else if (c == ']')
                    {
                        _doctypeBrackets--;
                    }
                    else if (c == '>' && _doctypeBrackets <= 0)
                    {
                        _mode = Mode.Content;
                    }

                    break;
                case Mode.Tag:
                    ReadTag(line, i);
                    break;
                default:
                    i = ReadContent(line, i);
                    break;
            }

            i++;
        }

        return Closed;
    }

    private int ReadContent(string line, int i)
    {
        if (line[i] != '<')
        {
            return i;
        }

        if (At(line, i, "<!--"))
        {
            _mode = Mode.Comment;
            return i + 3;
        }

        if (At(line, i, "<![CDATA["))
        {
            _mode = Mode.CData;
            return i + 8;
        }

        if (At(line, i, "<?"))
        {
            _mode = Mode.Instruction;
            return i + 1;
        }

        if (At(line, i, "<!"))
        {
            _mode = Mode.Doctype;
            _doctypeBrackets = 0;
            return i + 1;
        }

        _mode = Mode.Tag;
        _closingTag = At(line, i, "</");
        _quote = '\0';
        return _closingTag ? i + 1 : i;
    }

    private void ReadTag(string line, int i)
    {
        var c = line[i];
        if (_quote != '\0')
        {
            if (c == _quote)
            {
                _quote = '\0';
            }

            return;
        }

        if (c is '"' or '\'')
        {
            _quote = c;
            return;
        }

        if (c != '>')
        {
            return;
        }

        _mode = Mode.Content;
        if (_closingTag)
        {
            _depth--;
            Closed = _rootSeen && _depth <= 0;
        }
        else if (i > 0 && line[i - 1] == '/')
        {
            _rootSeen = true;
            Closed = _depth <= 0;
        }
        else
        {
            _depth++;
            _rootSeen = true;
        }
    }

    private static bool At(string line, int i, string token)
        => string.CompareOrdinal(line, i, token, 0, token.Length) == 0;
}
